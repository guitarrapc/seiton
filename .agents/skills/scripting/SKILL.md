---
name: scripting
description: Guidelines for writing shell scripts in this project, including which shell to use (pwsh for Windows, bash for Linux/Mac), single-line rule for PowerShell, and file encoding conventions (UTF-8 without BOM).
---

# Scripting Guidelines

Script Rule:

- Use pwsh for Windows scripts and bash for Linux/Mac scripts.
- Don't write any multi-line PowerShell Code in the shell. If you need to run a script, create a file then executte it.
- Treat file encoding as UTF-8 without BOM. If you need to read/write files, ensure they are UTF-8 encoded.
