package config

import (
	"fmt"
	"reflect"
	"strconv"
	"strings"
	"time"
)

// This file implements REQ-CFG-001 and REQ-CFG-041 by walking the struct rather
// than listing the keys. A list would go stale the moment a field is added, and
// a key with no override is exactly the failure REQ-CFG-001 forbids.

// EnvName renders the variable name for a dotted key path — REQ-CFG-041.
//
// The path is uppercased and every character outside A-Z and 0-9 becomes an
// underscore, so `server.http.address` is `WG_AGENT_SERVER_HTTP_ADDRESS`.
func EnvName(path string) string {
	var b strings.Builder
	b.WriteString(EnvPrefix)
	for _, r := range strings.ToUpper(path) {
		if (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') {
			b.WriteRune(r)
			continue
		}
		b.WriteByte('_')
	}
	return b.String()
}

// Keys lists every dotted key path, sorted by declaration order. The token
// command and the documentation generator both need the set, and deriving it
// here keeps one answer to what a key is.
func Keys() []string {
	var out []string
	walk(reflect.ValueOf(Default()), "", func(path string, _ reflect.Value) error {
		out = append(out, path)
		return nil
	})
	return out
}

// applyEnv overlays the environment on cfg. lookup is injected so a test does
// not have to mutate the process environment.
func applyEnv(cfg *Config, lookup func(string) (string, bool)) error {
	return walk(reflect.ValueOf(cfg).Elem(), "", func(path string, field reflect.Value) error {
		name := EnvName(path)
		raw, ok := lookup(name)
		if !ok {
			return nil
		}
		if err := assign(field, raw); err != nil {
			return fmt.Errorf("%s: %w", name, err)
		}
		return nil
	})
}

// walk visits every leaf field, building the dotted path from the yaml tags.
func walk(v reflect.Value, prefix string, fn func(string, reflect.Value) error) error {
	t := v.Type()
	for i := 0; i < t.NumField(); i++ {
		tag := t.Field(i).Tag.Get("yaml")
		if tag == "" || tag == "-" {
			continue
		}
		name := strings.Split(tag, ",")[0]
		path := name
		if prefix != "" {
			path = prefix + "." + name
		}

		field := v.Field(i)
		// A Duration is a struct-free named int64, so it is a leaf even though
		// its kind is Int64. Every other struct is a nesting level.
		if field.Kind() == reflect.Struct && field.Type() != reflect.TypeOf(time.Duration(0)) {
			if err := walk(field, path, fn); err != nil {
				return err
			}
			continue
		}
		if err := fn(path, field); err != nil {
			return err
		}
	}
	return nil
}

// assign parses raw into field. Only the kinds the configuration uses are
// handled; a new field of an unhandled kind fails loudly here rather than
// silently ignoring the operator's variable.
func assign(field reflect.Value, raw string) error {
	if !field.CanSet() {
		return fmt.Errorf("field is not settable")
	}

	if field.Type() == reflect.TypeOf(time.Duration(0)) {
		d, err := time.ParseDuration(raw)
		if err != nil {
			return fmt.Errorf("parse duration %q: %w", raw, err)
		}
		field.SetInt(int64(d))
		return nil
	}

	switch field.Kind() {
	case reflect.String:
		// Covers model.Axis and every plain string. Validate checks the value.
		field.SetString(raw)
	case reflect.Bool:
		b, err := strconv.ParseBool(raw)
		if err != nil {
			return fmt.Errorf("parse bool %q: %w", raw, err)
		}
		field.SetBool(b)
	case reflect.Int, reflect.Int64:
		n, err := strconv.ParseInt(raw, 10, 64)
		if err != nil {
			return fmt.Errorf("parse integer %q: %w", raw, err)
		}
		field.SetInt(n)
	default:
		return fmt.Errorf("no override defined for kind %s", field.Kind())
	}
	return nil
}
