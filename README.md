# Reference Highlight

<p align="center">
  <img src="Assets/ExtensionIcon.png" width="128" height="128" alt="Reference Highlight logo">
</p>

Reference Highlight is a Visual Studio extension that makes **Find All References** easier to scan by coloring read and write references differently.

Read references are highlighted in green by default. Write references are highlighted in pink by default. The result window is also enlarged for better readability, and the same reference data is used to highlight matching identifiers in open editor tabs.

<img width="790" height="436" alt="Reference Highlight preview" src="https://github.com/user-attachments/assets/814cf330-4cca-4dc5-b4cf-a6bcff357300" />

## Features

- Highlights read references and write references with separate colors.
- Applies the color to both the `Read/Write` column and the matching identifier in the code snippet.
- Enlarges Find All References result text for easier reading.
- Supports separate default colors for light and dark Visual Studio themes.
- Lets users customize read/write colors from Visual Studio options.
- Updates open editor highlights based on the latest Find All References result set.

## Customization

Open:

```text
Tools > Options > Reference Highlight > Colors
```

Available settings:

- `Light theme > Read color`
- `Light theme > Write color`
- `Dark theme > Read color`
- `Dark theme > Write color`

The extension automatically chooses black or white foreground text for contrast against the selected highlight color.

## Installation

Download or build the VSIX, then install:

```text
bin/Debug/net472/ReferenceHighlight.vsix
```

After installation, restart Visual Studio if prompted.

## Usage

1. In Visual Studio, place the caret on a symbol.
2. Run **Find All References**.
3. The references result window will show read references and write references with separate colors.
4. Open files from the result list to see matching editor highlights.

## Build

Requirements:

- Visual Studio with the Visual Studio SDK workload
- .NET SDK

Build from the repository root:

```powershell
dotnet build ReferenceHighlight.csproj
```

The VSIX is generated at:

```text
bin/Debug/net472/ReferenceHighlight.vsix
```

## Compatibility

The extension targets Visual Studio 17.0 or newer on amd64:

- Visual Studio Community
- Visual Studio Professional
- Visual Studio Enterprise

## Notes

Reference Highlight depends on the metadata exposed by Visual Studio's Find All References table. If a language service does not report read/write access information, those entries may not be classified as expected.
