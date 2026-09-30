# A node of a supported distribution, with systemd as PID 1 so wg-quick@ units run as they do on a
# host. --no-install-recommends keeps wireguard-dkms and a compiler out of the image.
FROM debian:13
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      systemd wireguard-tools iproute2 iputils-ping procps \
 && rm -rf /var/lib/apt/lists/*
STOPSIGNAL SIGRTMIN+3
CMD ["/lib/systemd/systemd"]
