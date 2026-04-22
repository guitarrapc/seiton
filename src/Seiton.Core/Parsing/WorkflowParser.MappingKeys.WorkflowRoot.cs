namespace Seiton.Core.Parsing;

/// <summary>Key tables for <see cref="WorkflowParser"/> root / defaults / concurrency mapping dispatch.</summary>
public static partial class WorkflowParser
{
    private readonly struct RootStructuralHintKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "jobs"u8,
            1 => "runs"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum WorkflowRootMappingKey : byte
    {
        Name = 0,
        RunName = 1,
        On = 2,
        Jobs = 3,
        Env = 4,
        Permissions = 5,
        Defaults = 6,
        Concurrency = 7,
    }

    private readonly struct WorkflowRootKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 8;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "name"u8,
            1 => "run-name"u8,
            2 => "on"u8,
            3 => "jobs"u8,
            4 => "env"u8,
            5 => "permissions"u8,
            6 => "defaults"u8,
            7 => "concurrency"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private static string WorkflowRootDuplicateKeyName(WorkflowRootMappingKey key) => key switch
    {
        WorkflowRootMappingKey.Name => "name",
        WorkflowRootMappingKey.RunName => "run-name",
        WorkflowRootMappingKey.On => "on",
        WorkflowRootMappingKey.Jobs => "jobs",
        WorkflowRootMappingKey.Env => "env",
        WorkflowRootMappingKey.Permissions => "permissions",
        WorkflowRootMappingKey.Defaults => "defaults",
        WorkflowRootMappingKey.Concurrency => "concurrency",
        _ => "workflow key",
    };

    private enum ActionMetadataRootMappingKey : byte
    {
        Description = 0,
        Inputs = 1,
        Outputs = 2,
        Runs = 3,
        Branding = 4,
    }

    private readonly struct ActionMetadataRootKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 5;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "description"u8,
            1 => "inputs"u8,
            2 => "outputs"u8,
            3 => "runs"u8,
            4 => "branding"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private static string ActionMetadataRootDuplicateKeyName(ActionMetadataRootMappingKey key) => key switch
    {
        ActionMetadataRootMappingKey.Description => "description",
        ActionMetadataRootMappingKey.Inputs => "inputs",
        ActionMetadataRootMappingKey.Outputs => "outputs",
        ActionMetadataRootMappingKey.Runs => "runs",
        ActionMetadataRootMappingKey.Branding => "branding",
        _ => "action metadata key",
    };

    private enum WorkflowDefaultsOuterMappingKey : byte
    {
        Run = 0,
    }

    private readonly struct WorkflowDefaultsOuterKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 1;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "run"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum DefaultsRunMappingKey : byte
    {
        Shell = 0,
        WorkingDirectory = 1,
    }

    private readonly struct DefaultsRunKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "shell"u8,
            1 => "working-directory"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private enum ConcurrencyMappingKey : byte
    {
        Group = 0,
        CancelInProgress = 1,
    }

    private readonly struct ConcurrencyKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 2;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "group"u8,
            1 => "cancel-in-progress"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }
}
