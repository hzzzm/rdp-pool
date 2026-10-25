# Prefer DaoCloud mirror when Docker Hub is slow/blocked on this network.
ARG GOLANG_IMAGE=docker.m.daocloud.io/library/golang:1.22-bookworm
ARG ALPINE_IMAGE=docker.m.daocloud.io/library/alpine:3.20
FROM ${GOLANG_IMAGE} AS build
WORKDIR /src
ENV GOPROXY=https://goproxy.cn,direct
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -o /out/rdp-pool-api ./cmd/api

# Alpine runtime (avoid gcr.io distroless — often blocked without proxy)
FROM ${ALPINE_IMAGE}
RUN sed -i 's#dl-cdn.alpinelinux.org#mirrors.aliyun.com#g' /etc/apk/repositories \
  && apk add --no-cache ca-certificates tzdata wget \
  && adduser -D -u 65532 nonroot
WORKDIR /
COPY --from=build /out/rdp-pool-api /rdp-pool-api
EXPOSE 8081
USER nonroot
ENTRYPOINT ["/rdp-pool-api"]
