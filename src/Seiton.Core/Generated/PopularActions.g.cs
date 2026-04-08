namespace Seiton.Core.Generated;

internal static class PopularActions
{
    internal enum ActionId
    {
        ActionsCheckout,
        ActionsSetupDotNet,
        ActionsSetupNode,
        ActionsCache,
        ActionsUploadArtifact,
        ActionsDownloadArtifact,
        DockerLoginAction,
    }

    internal readonly struct ActionSpec
    {
        internal ActionId Id { get; }

        internal ActionSpec(ActionId id)
        {
            Id = id;
        }

        internal bool IsInputAllowed(ReadOnlySpan<byte> inputNameUtf8)
        {
            return Id switch
            {
                ActionId.ActionsCheckout =>
                    EqualsAsciiIgnoreCase(inputNameUtf8, "repository"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "ref"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "token"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "ssh-key"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "ssh-known-hosts"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "ssh-strict"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "ssh-user"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "persist-credentials"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "path"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "clean"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "filter"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "sparse-checkout"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "sparse-checkout-cone-mode"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "fetch-depth"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "fetch-tags"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "show-progress"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "lfs"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "submodules"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "set-safe-directory"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "github-server-url"u8),
                ActionId.ActionsSetupDotNet =>
                    EqualsAsciiIgnoreCase(inputNameUtf8, "dotnet-version"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "global-json-file"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "source-url"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "owner"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "config-file"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "cache"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "cache-dependency-path"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "token"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "quality"u8),
                ActionId.ActionsSetupNode =>
                    EqualsAsciiIgnoreCase(inputNameUtf8, "node-version"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "node-version-file"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "architecture"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "check-latest"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "registry-url"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "scope"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "token"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "always-auth"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "cache"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "cache-dependency-path"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "package-manager-cache"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "mirror"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "mirror-token"u8),
                ActionId.ActionsCache =>
                    EqualsAsciiIgnoreCase(inputNameUtf8, "key"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "path"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "restore-keys"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "upload-chunk-size"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "enableCrossOsArchive"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "fail-on-cache-miss"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "lookup-only"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "save-always"u8),
                ActionId.ActionsUploadArtifact =>
                    EqualsAsciiIgnoreCase(inputNameUtf8, "name"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "path"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "if-no-files-found"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "retention-days"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "compression-level"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "overwrite"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "include-hidden-files"u8),
                ActionId.ActionsDownloadArtifact =>
                    EqualsAsciiIgnoreCase(inputNameUtf8, "name"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "path"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "pattern"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "github-token"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "repository"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "run-id"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "merge-multiple"u8),
                ActionId.DockerLoginAction =>
                    EqualsAsciiIgnoreCase(inputNameUtf8, "registry"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "username"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "password"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "ecr"u8)
                    || EqualsAsciiIgnoreCase(inputNameUtf8, "logout"u8),
                _ => false,
            };
        }
    }

    internal static bool TryGet(ReadOnlySpan<byte> usesUtf8, out ActionSpec spec)
    {
        if (MatchesActionReference(usesUtf8, "actions/checkout"u8))
        {
            spec = new ActionSpec(ActionId.ActionsCheckout);
            return true;
        }

        if (MatchesActionReference(usesUtf8, "actions/setup-dotnet"u8))
        {
            spec = new ActionSpec(ActionId.ActionsSetupDotNet);
            return true;
        }

        if (MatchesActionReference(usesUtf8, "actions/setup-node"u8))
        {
            spec = new ActionSpec(ActionId.ActionsSetupNode);
            return true;
        }

        if (MatchesActionReference(usesUtf8, "actions/cache"u8))
        {
            spec = new ActionSpec(ActionId.ActionsCache);
            return true;
        }

        if (MatchesActionReference(usesUtf8, "actions/upload-artifact"u8))
        {
            spec = new ActionSpec(ActionId.ActionsUploadArtifact);
            return true;
        }

        if (MatchesActionReference(usesUtf8, "actions/download-artifact"u8))
        {
            spec = new ActionSpec(ActionId.ActionsDownloadArtifact);
            return true;
        }

        if (MatchesActionReference(usesUtf8, "docker/login-action"u8))
        {
            spec = new ActionSpec(ActionId.DockerLoginAction);
            return true;
        }

        spec = default;
        return false;
    }

    static bool MatchesActionReference(ReadOnlySpan<byte> usesUtf8, ReadOnlySpan<byte> actionNameUtf8)
    {
        if (usesUtf8.IsEmpty)
        {
            return false;
        }

        if (usesUtf8.StartsWith("./"u8) || usesUtf8.StartsWith("../"u8) || usesUtf8.StartsWith("docker://"u8))
        {
            return false;
        }

        if (usesUtf8.Length < actionNameUtf8.Length)
        {
            return false;
        }

        if (!EqualsAsciiIgnoreCase(usesUtf8.Slice(0, actionNameUtf8.Length), actionNameUtf8))
        {
            return false;
        }

        return usesUtf8.Length == actionNameUtf8.Length || usesUtf8[actionNameUtf8.Length] == (byte)'@';
    }

    static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    static byte ToLowerAscii(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + 32)
            : value;
    }
}
