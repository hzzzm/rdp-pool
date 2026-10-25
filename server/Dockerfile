# Prefer DaoCloud mirror when docker.io is slow/blocked on CN networks.
FROM docker.m.daocloud.io/library/golang:1.22-bookworm AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -o /out/rdp-pool-api ./cmd/api

# Alpine runtime (avoid gcr.io distroless — often blocked without proxy)
FROM docker.m.daocloud.io/library/alpine:3.20
RUN apk add --no-cache ca-certificates tzdata wget \
  && adduser -D -u 65532 nonroot
WORKDIR /
COPY --from=build /out/rdp-pool-api /rdp-pool-api
EXPOSE 8081
USER nonroot
ENTRYPOINT ["/rdp-pool-api"]
