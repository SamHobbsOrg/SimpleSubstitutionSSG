Gemini said
This .editorconfig is quite comprehensive, especially regarding its strictness on null-analysis and explicit typing. I've synchronized the following copilot-instructions.md to ensure Gemini 3.1 Pro doesn't suggest code that would immediately trigger a warning in your IDE.

Notable syncs I've made:

Var usage: Your config is set to false:warning for almost all scenarios. I've explicitly told Gemini to use explicit types.

Primary Constructors: Your config prefers them (true:warning), so I've prioritized those for new C# classes.

Expression Bodies: You allow them for properties/accessors but prefer block bodies for methods/constructors. I've mirrored that logic.

.github/copilot-instructions.md
1. General Principles
Code Authority: Always defer to the root .editorconfig for formatting, indentation, and naming. If a suggestion conflicts with .editorconfig, prioritize the config file.

Modern C# Preferences: Prioritize C# 12+ features that align with current project settings, specifically Primary Constructors (csharp_style_prefer_primary_constructors = true).

Veteran Readability: Maintain the logic parity of legacy systems (COBOL/Mainframe) while using modern .NET safety features.

2. Naming & Type Conventions (Synced with .editorconfig)
Explicit Typing: DO NOT use var for built-in types (int, string, bool) or even when the type is apparent. Always use explicit types (e.g., List<string> items = new();) as per csharp_style_var_for_built_in_types = false.

Member Access: Do not qualify members with this. or Me. unless necessary to resolve ambiguity (dotnet_style_qualification_for_field = false).

Naming Styles: * Interfaces: Must begin with I (e.g., IService).

Types & Non-fields: Classes, Structs, Enums, Properties, and Methods must use PascalCase.

Nullables: Strictly adhere to nullable reference type annotations. Handle all potential nulls to avoid CS8600–CS8604 warnings.

3. Expression & Block Preferences
Expression-Bodied Members: Use arrow expressions (=>) for properties and simple accessors. Use block bodies { } for methods, constructors, and operators.

Braces: Braces are preferred for clarity, even for single-line statements (csharp_prefer_braces = true).

Namespace: Use File-scoped namespaces (e.g., namespace MyProject.Services;) to reduce indentation levels.

4. Legacy Modernization & Logic
COBOL Refactoring: When translating COBOL logic, maintain the exact business math and conditional flow. Use modern C# collection expressions ([]) for array/list initializations where types match.

Error Handling: Use structured try-catch blocks. As a veteran-led project, ensure exceptions are caught specifically and logged via the internal LegacyLogger.

Documentation: While the .editorconfig relaxes some StyleCop documentation rules, all public members should still include XML /// summaries for maintainability.

5. Defensive Programming
Null Checks: Prefer is null or is not null over reference equality checks.

Pattern Matching: Use switch expressions and pattern matching over traditional if/else or as casts where it improves readability.
