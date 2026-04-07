# Coding Style Guidelines

## Modern C# Features

- Use the latest C# syntax available (C# 13 or later)
- Use `using` declarations for automatic disposal
- Use file-scoped namespaces to reduce indentation
- Use collection literals where applicable
- Use pattern matching and switch expressions

## Naming Conventions

- Follow .NET coding guidelines for base conventions
- Use `PascalCase` for constant names
- Do NOT use `_` or `s_` prefixes for fields
- Omit the `private` modifier (it's the default)
- Prefer the use of `var` for local variables

## Unit Tests

- All sorting algorithms must have comprehensive unit tests
- Tests should cover edge cases (empty arrays, single elements, duplicates, etc.)
- Test file naming: `{AlgorithmName}Tests.cs`
- Use xUnit framework conventions
