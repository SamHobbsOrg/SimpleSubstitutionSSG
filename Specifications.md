# Simple Substitution Static Sites Generator

Create a simple substitution generator for static sites to be used as a GitHub Action that can be executed as a Ubuntu Runner. The input will be a configuration file, an input directory and a snippets directory and the output will be an output directory in which html files will be modified. Use GitHub Actions error/warning annotations. When run as a GitHub Action the program will execute as a Ubuntu application in a Docker container. When run locally for testing it will execute as a Windows application. The contents of `output_dir` (see below) will separately be deployed as the static website.


## Configuration File

The configuration file is a yaml file in the following format.

```yaml
input_dir: "{dir}"
output_dir: "{dir}"
snippets_dir: "{dir}"

block_substitutions:    # list of zero or more objects, each with:
  - name: "{blockname}"    # required
    file: "{filename}"  	# required, relative path

inline_substitutions:     # list of zero or more objects, each with:
  - name: "{substitutionname}"     # required
    text: "{substitutiontext}"	 # required
```

Where `block_substitutions` and `inline_substitutions` are arrays of objects.

If the configuration file has invalid yaml then issue error message MSG003 and exit with code 1. (MSG003 will be improved based on parser output after the code is generated.)

When a configuration list contains duplicate name values (within the same list) the first occurrence is to be used; for each subsequent duplicate emit warning MSG012 (use the parser node line number for the annotation) and ignore the duplicate entry; comparisons are case‑insensitive. 

A `blockname` can be the same as a `substitutionname` and both are case-insensitive.

If the configuration file contains any top-level keys other than `input_dir`, `output_dir`, `snippets_dir`, `block_substitutions`, and `inline_substitutions`, issue error message MSG001 and exit with code 2. If any entry in `block_substitutions` contains keys other than `name` and `file`, or any entry in `inline_substitutions` contains keys other than `name` and `text`, issue error message MSG002 and exit with code 3.

For `input_dir` use `src` as the default if not specified. For `output_dir` use `public` as the default if not specified. For `snippets_dir` use `snippets` as the default if not specified.

Issue error message MSG004 and exit with code 4 when the specified `input_dir` does not exist. Issue error message MSG005 and exit with code 5 when the specified `snippets_dir` does not exist. Issue error message MSG015, MSG016 or MSG017 (whichever is appropriate) then exit with code 6 when either `input_dir`, `output_dir` or `snippets_dir` are the same. Issue error message MSG007 and exit with code 7 when either `input_dir`, `output_dir` or `snippets_dir` overlap (one is a subdirectory of the other).

If a file with `filename` does not exist then show warning message MSG010 and ignore the `blockname`.

## Processing

Create `output_dir` if it doesn't exist and clear it if it does. Issue message MSG013 and exit with code 8 if the directory cannot be created. Issue message MSG014 and exit with code 9 if the directory cannot be cleared. 

Cache the contents of `block_substitutions` files and support processing of html files in parallel. Make the snippet cache thread-safe and immutable once built.

Use iteration instead of recursion to copy all files, directories and subdirectories from the `input_dir` directory to the `output_dir` directory. Copy file contents only, do not copy timestamps, permissions and extended attributes.

Process every file in the output directory and subdirectories that have a `html` extension in the following manner.

The contents of each html file will have comments containing a tag as specified below and a name replaced. All other contents will not be changed.

If an input HTML file contains a BOM then emit warning MSG008 and preserve the input file’s BOM and encoding when writing the output file; snippet files must have any BOM removed before their contents are inserted (inserted snippets must not reintroduce a BOM), and encoding conversions should preserve the original HTML file encoding where possible.

The HTML comments (SSG block markers) are in one of the following two forms:

`<!--ssgb {name}-->` — marks the beginning of a named block
`<!--ssgi {name}-->` — marks an inline (or include) reference

If (`ssgb` or `ssgi`) is not followed by whitespace then ignore the comment. Whitespace may optionally appear between `<!--` and the tag keyword, and between the name and `-->`. No whitespace can be in `name` and `name` is case insensitive.

Allow `ssgb` and `ssgi` comments with the same name to exist multiple times in a html file and processed as in the following.

When a named block comment is encountered and `name` matches a `blockname` in the `block_substitutions` section then replace (first remove) the comment with the contents of the file in the `snippets_dir` directory with the filename in the corresponding `filename`.

If a snippet has a BOM then remove the BOM before inserting the contents in the output. If the name does not match then show warning message MSG009 and leave the comment as-is. When an inline reference tag is encountered and `name` matches a `substitutionname` in the `inline_substitutions` section then replace (first remove) the comment with the corresponding `substitutiontext`. If the name does not match then show warning message MSG011 and leave the comment as-is.

All relative paths in the configuration are interpreted relative to the repository root (the GitHub Actions workspace, GITHUB_WORKSPACE) when running as an action. When running locally for tests, relative paths are interpreted relative to the process working directory. Absolute paths are accepted unchanged.

Before any directory-equality or overlap checks, the tool canonicalizes paths by calling Path.GetFullPath, normalizing separators, removing trailing separators, and — when possible — resolving symlinks to their targets. Path comparisons use platform-appropriate case sensitivity (case-insensitive on Windows, case-sensitive on Unix). Equality is determined by comparing canonicalized absolute paths; A is considered a subdirectory of B if A begins with B + path separator after canonicalization.

## Messages

| ID | Level | File | Line | Message Template |
|----|-------|------|------|-----------------|
| MSG001 | error | conf. | conf. file line | `Configuration file unknown top-level key: {key}` |
| MSG002 | error | conf. | conf. file line | `Invalid key {key}` |
| MSG003 | error | conf. | conf. file line | `Invalid yaml, reason: {reason}` |
| MSG004 | error | conf. | conf. file line | `Input directory {input_dir} does not exist` |
| MSG005 | error | conf. | conf. file line | `Snippets directory {snippets_dir} does not exist` |
| MSG007 | error | conf. | conf. file line | `Overlapping directory, {directory} is a subdirectory of {directory}` |
| MSG008 | warning | html output | 1 | `File has a BOM` |
| MSG009 | warning | html | html line | `Block name {name} not found` |
| MSG010 | warning | conf. | conf. file line | `File {filename} does not exist` |
| MSG011 | warning | html | html line | `Substitution name {name} not found` |
| MSG012 | warning | conf. | conf. file line | `Duplicate name {name} in block {key}` |
| MSG013 | error | conf. | conf. file line | `Output directory cannot be created, error: {reason}` |
| MSG014 | error | conf. | conf. file line | `Output directory cannot be cleared, error: {reason}` |
| MSG015 | error | conf. | conf. file line | `input_dir and output_dir resolve to the same path` |
| MSG016 | error | conf. | conf. file line | `input_dir and snippets_dir resolve to the same path` |
| MSG017 | error | conf. | conf. file line | `output_dir and snippets_dir resolve to the same path` |

For MSG001, MSG002, MSG003, MSG010 and MSG012 use parser node line numbers.

For MSG009 and MSG011 use the line where the comment appears in the input HTML.
