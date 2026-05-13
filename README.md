# Fizzle

A minimal Lingo transpiler, copied from [Drizzle](https://github.com/SlimeCubed/Drizzle) and modified to target Lua, with some generalizations.

## Installation

Clone the repo and move to it
```bash
git clone https://github.com/HenryMarkle/Fizzle.git
cd Fizzle
```

Build the project
```bash
dotnet publish -c Release
```

The program can be found in `bin/Release/net10.0/linux-x64/publish`.
Or if you're on Windows: `bin/Release/net10.0/win-x64/publish`.

## Usage

```bash
Fizzle -f <FILE> -o <OUTPUT>
```

`-f` is the input file to be parsed. It can accept either multiple files or a folder:
- If a file path is provided then that file will be parsed.
- If a folder is given, then all files inside will be parsed.

`-o` is the output folder path in which the transpiled lingo sources will be.

### Examples

```bash
Fizzle -f effects.lingo
```

```bash
Fizzle -f lvlEd.ls,renderLight.ls,propEditor.ls -out special
```

Both `.lingo` and `.ls` are supported. This can configured with the `-e` flag:

```bash
Fizzle -e .txt -f LingoSource
```