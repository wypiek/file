# syntax=docker/dockerfile:1
# Build Caddy from source. No caddy image is pulled at any stage.
FROM golang:1.25-alpine AS build
ARG CADDY_VERSION=v2.10.2
# Optional CA bundle for builds inside a managed HTTPS proxy environment.
RUN --mount=type=secret,id=ca_bundle \
    SSL_CERT_FILE=$([ -f /run/secrets/ca_bundle ] && echo /run/secrets/ca_bundle || echo /etc/ssl/certs/ca-certificates.crt) \
    CGO_ENABLED=0 GOBIN=/out go install github.com/caddyserver/caddy/v2/cmd/caddy@${CADDY_VERSION}

FROM alpine:3.22
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
COPY --from=build /out/caddy /usr/bin/caddy
ENV XDG_CONFIG_HOME=/config XDG_DATA_HOME=/data
EXPOSE 80 443 443/udp
ENTRYPOINT ["caddy"]
CMD ["run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
