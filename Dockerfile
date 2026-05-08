# NativeAOT Linux binary (glibc). CI supplies amd64/seiton and arm64/seiton in the build context.
FROM gcr.io/distroless/base-debian12:latest
ARG TARGETARCH
COPY ${TARGETARCH}/seiton /seiton
ENTRYPOINT ["/seiton"]
