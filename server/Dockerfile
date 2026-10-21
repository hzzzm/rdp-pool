FROM golang:1.22-bookworm AS build
WORKDIR /src
COPY go.mod go.sum* ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -o /out/rdp-pool-api ./cmd/api

FROM gcr.io/distroless/static-debian12:nonroot
WORKDIR /
COPY --from=build /out/rdp-pool-api /rdp-pool-api
EXPOSE 8081
USER nonroot:nonroot
ENTRYPOINT ["/rdp-pool-api"]
