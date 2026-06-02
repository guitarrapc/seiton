# SARIF Validator

- sarif files are output by running seiton on guitarrapc/githubactions-lab: seiton.sarif
- Validated sarif with following official validator: https://sarifweb.azurewebsites.net/Validation

There are 82 issues in total, with 80 of them being SARIF1002 and 1 each of SARIF2005 and SARIF2004. Below are the details of these issues.

## SARIF1002 (80)

SARIF1002: Specify a valid URI reference for every URI-valued property. URIs must conform to [RFC 3986](https://tools.ietf.org/html/rfc3986). In addition, 'file' URIs must not include '..' segments. If symbolic links are present, '..' might have different meanings on the machine that produced the log file and the machine where an end user or a tool consumes it.

Details. (Detected 80 related until `runs[0].results[79].locations[0]`.)

```
SARIF1002: runs[0].results[0].locations[0].physicalLocation.artifactLocation.uri: The string 'C:\github\guitarrapc\githubactions-lab\.github\workflows\_reusable-dump-context.yaml' is not a valid URI reference. URIs must conform to [RFC 3986](https://tools.ietf.org/html/rfc3986).
```


## SARIF2005 (1)

SARIF2005: Provide information that makes it easy to identify the name and version of your tool. The tool's 'name' property should be no more than three words long. This makes it easy to remember and allows it to fit into a narrow column when displaying a list of results. If you need to provide more information about your tool, use the 'fullName' property. The tool should provide either or both of the 'version' and 'semanticVersion' properties. This enables the log file consumer to determine whether the file was produced by an up to date version, and to avoid accidentally comparing log files produced by different tool versions. If 'version' is used, facilitate comparison between versions by specifying a version number that starts with an integer, optionally followed by any desired characters.

Details.

```
SARIF2005: runs[0].tool.driver: The tool 'seiton' does not provide any of the version-related properties 'version', 'semanticVersion', 'dottedQuadFileVersion'. Providing version information enables the log file consumer to determine whether the file was produced by an up to date version, and to avoid accidentally comparing log files produced by different tool versions.
```

## SARIF2004 (1)

SARIF2004: Emit arrays only if they provide additional information. In several parts of a SARIF log file, a subset of information about an object appears in one place, and the full information describing all such objects appears in an array elsewhere in the log file. For example, each 'result' object has a 'ruleId' property that identifies the rule that was violated. Elsewhere in the log file, the array 'run.tool.driver.rules' contains additional information about the rules. But if the elements of the 'rules' array contained no information about the rules beyond their ids, then there might be no reason to include the 'rules' array at all, and the log file could be made smaller simply by omitting it. In some scenarios (for example, when assessing compliance with policy), the 'rules' array might be used to record the full set of rules that were evaluated. In such a scenario, the 'rules' array should be retained even if it contains only id information. Similarly, most 'result' objects contain at least one 'artifactLocation' object. Elsewhere in the log file, the array 'run.artifacts' contains additional information about the artifacts that were analyzed. But if the elements of the 'artifacts' array contained not information about the artifacts beyond their locations, then there might be no reason to include the 'artifacts' array at all, and again the log file could be made smaller by omitting it. In some scenarios (for example, when assessing compliance with policy), the 'artifacts' array might be used to record the full set of artifacts that were analyzed. In such a scenario, the 'artifacts' array should be retained even if it contains only location information. In addition to the avoiding unnecessary arrays, there are other ways to optimize the size of SARIF log files. Prefer the result object properties 'ruleId' and 'ruleIndex' to the nested object-valued property 'result.rule', unless the rule comes from a tool component other than the driver (in which case only 'result.rule' can accurately point to the metadata for the rule). The 'ruleId' and 'ruleIndex' properties are shorter and just as clear. Do not specify the result object's 'analysisTarget' property unless it differs from the result location. The canonical scenario for using 'result.analysisTarget' is a C/C++ language analyzer that is instructed to analyze example.c, and detects a result in the included file example.h. In this case, 'analysisTarget' is example.c, and the result location is in example.h.

Details.

```
SARIF2004: The 'rules' array at 'runs[0].tool.driver.rules' contains no information beyond the ids of the rules. Removing this array might reduce the log file size without losing information. In some scenarios (for example, when assessing compliance with policy), the 'rules' array might be used to record the full set of rules that were evaluated. In such a scenario, the 'rules' array should be retained even if it contains only id information.
```
