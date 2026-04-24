namespace Seiton.Core.Parsing;

/// <summary>Additional UTF-8 key tables for strategy, containers, action metadata, and on.* mapping dispatch.</summary>
public static partial class WorkflowParser
{
    private enum StrategyMappingKey : byte
    {
        Matrix = 0,
        FailFast = 1,
        MaxParallel = 2,
    }

    private readonly struct StrategyKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 3;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "matrix"u8,
            1 => "fail-fast"u8,
            2 => "max-parallel"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private readonly struct MatrixIncludeExcludeKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "exclude"u8,
            1 => "include"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum ContainerMappingKey : byte
    {
        Image = 0,
        Credentials = 1,
        Env = 2,
        Ports = 3,
        Volumes = 4,
        Options = 5,
    }

    private readonly struct ContainerKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 6;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "image"u8,
            1 => "credentials"u8,
            2 => "env"u8,
            3 => "ports"u8,
            4 => "volumes"u8,
            5 => "options"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private static string ContainerDuplicateSubKey(ContainerMappingKey key) => key switch
    {
        ContainerMappingKey.Image => "image",
        ContainerMappingKey.Credentials => "credentials",
        ContainerMappingKey.Env => "env",
        ContainerMappingKey.Ports => "ports",
        ContainerMappingKey.Volumes => "volumes",
        ContainerMappingKey.Options => "options",
        _ => "key",
    };

    private enum CredentialsMappingKey : byte
    {
        Username = 0,
        Password = 1,
    }

    private readonly struct CredentialsKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "username"u8,
            1 => "password"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum ActionMetadataInputOptionKey : byte
    {
        Description = 0,
        Required = 1,
        Default = 2,
        DeprecationMessage = 3,
    }

    private readonly struct ActionMetadataInputOptionKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 4;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "description"u8,
            1 => "required"u8,
            2 => "default"u8,
            3 => "deprecationMessage"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum ActionMetadataOutputOptionKey : byte
    {
        Description = 0,
        Value = 1,
    }

    private readonly struct ActionMetadataOutputOptionKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "description"u8,
            1 => "value"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum ActionMetadataBrandingKey : byte
    {
        Icon = 0,
        Color = 1,
    }

    private readonly struct ActionMetadataBrandingKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "icon"u8,
            1 => "color"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum ActionMetadataRunsMappingKey : byte
    {
        Using = 0,
        Main = 1,
        Pre = 2,
        Post = 3,
        PreIf = 4,
        PostIf = 5,
        Image = 6,
        Entrypoint = 7,
        Args = 8,
        Env = 9,
        Steps = 10,
    }

    private readonly struct ActionMetadataRunsKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 11;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "using"u8,
            1 => "main"u8,
            2 => "pre"u8,
            3 => "post"u8,
            4 => "pre-if"u8,
            5 => "post-if"u8,
            6 => "image"u8,
            7 => "entrypoint"u8,
            8 => "args"u8,
            9 => "env"u8,
            10 => "steps"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private static string ActionMetadataRunsDuplicateKeyName(ActionMetadataRunsMappingKey key) => key switch
    {
        ActionMetadataRunsMappingKey.Using => "using",
        ActionMetadataRunsMappingKey.Main => "main",
        ActionMetadataRunsMappingKey.Pre => "pre",
        ActionMetadataRunsMappingKey.Post => "post",
        ActionMetadataRunsMappingKey.PreIf => "pre-if",
        ActionMetadataRunsMappingKey.PostIf => "post-if",
        ActionMetadataRunsMappingKey.Image => "image",
        ActionMetadataRunsMappingKey.Entrypoint => "entrypoint",
        ActionMetadataRunsMappingKey.Args => "args",
        ActionMetadataRunsMappingKey.Env => "env",
        ActionMetadataRunsMappingKey.Steps => "steps",
        _ => "runs key",
    };

    private enum OnScheduleEntryMappingKey : byte
    {
        Cron = 0,
        Timezone = 1,
    }

    private readonly struct OnScheduleEntryKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "cron"u8,
            1 => "timezone"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private readonly struct OnWorkflowDispatchTopKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 1;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "inputs"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum WorkflowDispatchInputFieldKey : byte
    {
        Description = 0,
        Required = 1,
        Default = 2,
        Type = 3,
        Options = 4,
    }

    private readonly struct WorkflowDispatchInputFieldKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 5;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "description"u8,
            1 => "required"u8,
            2 => "default"u8,
            3 => "type"u8,
            4 => "options"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private readonly struct DispatchInputTypeScalarKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 5;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "boolean"u8,
            1 => "choice"u8,
            2 => "environment"u8,
            3 => "number"u8,
            4 => "string"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum WorkflowCallEventMappingKey : byte
    {
        Inputs = 0,
        Secrets = 1,
        Outputs = 2,
    }

    private readonly struct WorkflowCallEventKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 3;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "inputs"u8,
            1 => "secrets"u8,
            2 => "outputs"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum WorkflowCallInputFieldKey : byte
    {
        Description = 0,
        Required = 1,
        Default = 2,
        Type = 3,
    }

    private readonly struct WorkflowCallInputFieldKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 4;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "description"u8,
            1 => "required"u8,
            2 => "default"u8,
            3 => "type"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private readonly struct WorkflowCallInputTypeScalarKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 3;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "boolean"u8,
            1 => "number"u8,
            2 => "string"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum WorkflowCallSecretFieldKey : byte
    {
        Description = 0,
        Required = 1,
    }

    private readonly struct WorkflowCallSecretFieldKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "description"u8,
            1 => "required"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum WorkflowCallOutputFieldKey : byte
    {
        Description = 0,
        Value = 1,
    }

    private readonly struct WorkflowCallOutputFieldKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "description"u8,
            1 => "value"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private readonly struct OnRepositoryDispatchKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 1;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "types"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum OnImageVersionMappingKey : byte
    {
        Names = 0,
        Versions = 1,
    }

    private readonly struct OnImageVersionKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "names"u8,
            1 => "versions"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    /// <summary>Webhook event mapping keys: ordinals align with <c>seen</c> bits in webhook option parsing.</summary>
    private enum OnWebhookEventOptionMappingKey : byte
    {
        Types = 0,
        Branches = 1,
        BranchesIgnore = 2,
        Tags = 3,
        TagsIgnore = 4,
        Paths = 5,
        PathsIgnore = 6,
        Workflows = 7,
    }

    private readonly struct OnWebhookEventOptionKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 8;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "types"u8,
            1 => "branches"u8,
            2 => "branches-ignore"u8,
            3 => "tags"u8,
            4 => "tags-ignore"u8,
            5 => "paths"u8,
            6 => "paths-ignore"u8,
            7 => "workflows"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    /// <summary>Webhook <c>on.*</c> options including <c>inputs</c>/<c>secrets</c>/<c>outputs</c> stubs for shallow parse.</summary>
    private enum OnEventOptionsExtendedMappingKey : byte
    {
        Types = 0,
        Branches = 1,
        BranchesIgnore = 2,
        Tags = 3,
        TagsIgnore = 4,
        Paths = 5,
        PathsIgnore = 6,
        Workflows = 7,
        Inputs = 8,
        Secrets = 9,
        Outputs = 10,
    }

    private readonly struct OnEventOptionsExtendedKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 11;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "types"u8,
            1 => "branches"u8,
            2 => "branches-ignore"u8,
            3 => "tags"u8,
            4 => "tags-ignore"u8,
            5 => "paths"u8,
            6 => "paths-ignore"u8,
            7 => "workflows"u8,
            8 => "inputs"u8,
            9 => "secrets"u8,
            10 => "outputs"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }
}
