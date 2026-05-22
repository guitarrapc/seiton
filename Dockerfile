# NativeAOT Linux binary (glibc). CI supplies amd64/seiton and arm64/seiton in the build context.
FROM gcr.io/distroless/base-debian13:nonroot@sha256:fb282f8ed3057f71dbfe3ea0f5fa7e961415dafe4761c23948a9d4628c6166fe
ARG TARGETARCH
WORKDIR /repo
COPY ${TARGETARCH}/seiton /seiton
ENTRYPOINT ["/seiton"]
