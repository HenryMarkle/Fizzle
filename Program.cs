using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Fizzle.Parser;
using Pidgin;

using Fizzle.Data;

namespace Fizzle;

internal sealed class GlobalContext(HashSet<string> movieHandlers, string sourcesDest)
{
    public HashSet<string> AllGlobals { get; } = new(StringComparer.InvariantCultureIgnoreCase);
    public Dictionary<string, string> GlobalTypes { get; } = new(StringComparer.InvariantCultureIgnoreCase);
    public HashSet<string> MovieHandlers { get; } = movieHandlers;
    public string SourcesDest { get; } = sourcesDest;
}

internal sealed class ScriptContext(
    GlobalContext parent,
    HashSet<string> allGlobals,
    HashSet<string> allHandlers,
    bool isMovieScript)
{
    public GlobalContext Parent { get; } = parent;
    public HashSet<string> AllGlobals { get; } = allGlobals;
    public HashSet<string> AllHandlers { get; } = allHandlers;
    public bool IsMovieScript { get; } = isMovieScript;
}

internal sealed class HandlerContext(ScriptContext parent, string name, TextWriter writer)
{
    public HashSet<string> Globals { get; } = new(StringComparer.InvariantCultureIgnoreCase);
    public HashSet<string> Locals { get; } = new(StringComparer.InvariantCultureIgnoreCase);
    public HashSet<string> DeclaredLocals { get; } = new(StringComparer.InvariantCultureIgnoreCase);
    public readonly Dictionary<string, string> Types = new(StringComparer.InvariantCultureIgnoreCase);
    public ScriptContext Parent { get; } = parent;
    public string Name { get; } = name;
    public TextWriter Writer { get; } = writer;
}

public sealed class ScriptQuirks
{
    public readonly HashSet<string> BlackListHandlers = new(StringComparer.InvariantCultureIgnoreCase);
}

internal sealed class ExpressionParams
{
    public bool WantBool;
    public bool BoolGranted;
}

public class Config
{
    public HashSet<string> MovieScripts = [
            "testDraw",
            "stop",
            "spelrelaterat",
            "ropeModel",
            "lvl",
            "levelRendering",
            "fiffigt",
            "TEdraw",
            "FILE",
            "comEditorUtils",
            "LSlime",
            "LMats",
            "cappinEditorUtils",
            "fezTreeRenderer"
        ];

    public HashSet<string> ParentScripts =
    [
        "PNG_encode",
            "levelEdit_parentscript"
    ];

    public HashSet<string> SkipScripts = [];

    public Dictionary<string, ScriptQuirks> Quirks = new()
    {
        ["fiffigt"] = new ScriptQuirks
        {
            BlackListHandlers = { "giveHitSurf", "cacheloadimage" }
        },
        ["PNG_encode"] = new ScriptQuirks
        {
            BlackListHandlers =
                {
                    "png_encode", "writeChunk", "writeBytes", "writeInt", "gzcompress", "writeCRC", "lingo_crc32",
                    "bitShift8", "xtraPresent"
                }
        }
    };

    public Dictionary<string, string> TypeKeywords = new()
        {
            { "point", "LingoPoint" },
            { "rect", "LingoRect" },
            { "list", "LingoList" },
            { "proplist", "LingoPropertyList" },
            { "number", "LingoNumber" },
            { "color", "LingoColor" },
            { "image", "LingoImage" },
            { "member", "CastMember" }
        };

    public string OutputNamespace = "Drizzle.Ported";

    public string[] InputFiles = [];
    public string OutputFileOrDir = "";
    public string? WorkingDir;
    public HashSet<string> FileExtensions = [".lingo", ".ls"];

    public static Config FromArgs(string[] args)
    {
        var config = new Config();

        var ext = getOptionValue("-e");
        var input = getOptionValue("-f");
        var output = getOptionValue("-o");

        if (ext is not null)
        {
            config.FileExtensions = [.. ext.Split(',')];
            if (config.FileExtensions.Count == 0)
                throw new ArgumentException("Invalid -e option value");
        }

        if (input is null)
            throw new ArgumentException("No -f option specified");

        if (Directory.Exists(input))
        {
            config.InputFiles = [.. Directory.GetFiles(input).Where(f => config.FileExtensions.Contains(Path.GetExtension(f)))];
        }
        else if (input.Split(',') is { Length: > 0 } files)
        {
            config.InputFiles = [.. files.Where(f => config.FileExtensions.Contains(Path.GetExtension(f)))];
        }

        //

        config.WorkingDir ??= AppContext.BaseDirectory
            ?? throw new Exception("Unnable to obtain executable's folder path");

        if (output is null)
            output = Path.Combine(config.WorkingDir, "out");
        else
            output = Path.GetFullPath(output);

        config.OutputFileOrDir = output;

        return config;

        //

        string? getOptionValue(string option)
        {
            var index = Array.IndexOf(args, option);
            if (index == -1) return null;

            if (index + 1 >= args.Length)
                return null;

            return args[index + 1];
        }

        // bool getOptionBool(string option) => Array.Exists(args, o => o == option);
    }
}

public class Program
{
    private static Config Config = new();
    private static readonly HashSet<string> LuaPreservedKeywords = [
        "local",
    ];

    public static void Main(string[] args)
    {
        // START ------------------------------------------------------------------

        var enUs = CultureInfo.GetCultureInfoByIetfLanguageTag("en-US");
        CultureInfo.DefaultThreadCurrentCulture = enUs;
        CultureInfo.DefaultThreadCurrentUICulture = enUs;

        // Parsing arguments ------------------------------------------------------

        if (args is [] or ["-h"] or ["--help"])
            Console.WriteLine(
@"Fizzle - minimal Lingo transpiler
Usage: <exe> -f=<INPUT> [OPTIONS]
    -f  Input file(s)/directory
        -f source.lingo,source2.lingo,source3.lingo
        OR
        -f sourceDir
Options
    -o  Output file/directory
        -o output.lua
        OR
        -o outputDir"
            );

        Config = Config.FromArgs(args);

        if (Config.InputFiles.Length > 0 && !Directory.Exists(Config.OutputFileOrDir))
        {
            Directory.CreateDirectory(Config.OutputFileOrDir);
        }

        // Parsing scripts --------------------------------------------------------

        Dictionary<string, AstNode.Script> scripts = Config.InputFiles
            .Where(f => !Config.SkipScripts.Contains(f))
            .AsParallel()
            .Select(f =>
            {
                using var reader = new StreamReader(f);

                var script = LingoParser.Script.Parse(reader);
                if (!script.Success)
                    throw new Exception($"Failed to parse script file {f}\n\n\t{script.Error?.RenderErrorMessage() ?? "<NULL>"}");

                return (
                    name: Path.GetFileNameWithoutExtension(f),
                    script: script.Value
                );
            })
            .ToDictionary(n => n.name, n => n.script);

        // Categorizing scripts ---------------------------------------------------

        var movieScripts = scripts.Where(kv => Config.MovieScripts.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var parentScripts = scripts.Where(kv => Config.ParentScripts.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var behaviorScripts = scripts.Except(movieScripts).Except(parentScripts)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var movieHandlers = movieScripts.Values
            .SelectMany(s => s.Nodes)
            .OfType<AstNode.Handler>()
            .Select(h => h.Name)
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);

        var globalContext = new GlobalContext(movieHandlers, sourcesDest: Config.OutputFileOrDir);

        // Output movie scripts ---------------------------------------------------

        OutputMovieScripts(movieScripts, globalContext);
        OutputParentScripts(parentScripts, globalContext);
        OutputBehaviorScripts(behaviorScripts, globalContext);

        OutputMovieGlobals(globalContext);

        // ------------------------------------------------------------------------
    }

    private static void OutputBehaviorScripts(
        IEnumerable<KeyValuePair<string, AstNode.Script>> scripts,
        GlobalContext ctx)
    {
        foreach (var (name, script) in scripts.OrderBy(pair => pair.Key))
        {
            var path = Path.Combine(ctx.SourcesDest, $"{name}.lua");
            using var file = new StreamWriter(path);

            OutputSingleBehaviorScript(name, script, file, ctx);
        }
    }

    private static void OutputSingleBehaviorScript(
        string name,
        AstNode.Script script,
        TextWriter writer,
        GlobalContext ctx)
    {
        WriteFileHeader(writer);
        writer.WriteLine($"--Behavior script: {name}\n");

        EmitScriptBody(name, script, writer, ctx, isMovieScript: false);

        // End class and namespace.
    }

    private static void OutputParentScripts(
        IEnumerable<KeyValuePair<string, AstNode.Script>> scripts,
        GlobalContext ctx)
    {
        foreach (var (name, script) in scripts.OrderBy(pair => pair.Key))
        {
            var path = Path.Combine(ctx.SourcesDest, $"Parent.{name}.lua");
            using var file = new StreamWriter(path);

            OutputSingleParentScript(name, script, file, ctx);
        }
    }

    private static void OutputSingleParentScript(
        string name,
        AstNode.Script script,
        TextWriter writer,
        GlobalContext ctx)
    {
        WriteFileHeader(writer);
        writer.WriteLine($"-- Parent script: {name}\n");

        EmitScriptBody(name, script, writer, ctx, isMovieScript: false);

        // End class and namespace.
    }

    private static void OutputMovieGlobals(GlobalContext ctx)
    {
        var path = Path.Combine(ctx.SourcesDest, "Movie._globals.lua");
        using var file = new StreamWriter(path);

        WriteFileHeader(file);
        file.WriteLine($"-- Movie globals\n");

        foreach (var glob in ctx.AllGlobals)
        {
            // var type = MapType(glob, ctx.GlobalTypes);
            file.WriteLine($"local {glob}");
        }
    }

    private static void OutputMovieScripts(
        IEnumerable<KeyValuePair<string, AstNode.Script>> scripts,
        GlobalContext ctx)
    {
        foreach (var (name, script) in scripts.OrderBy(pair => pair.Key))
        {
            var path = Path.Combine(ctx.SourcesDest, $"Movie.{name}.lua");
            Directory.CreateDirectory(ctx.SourcesDest);
            using var file = new StreamWriter(path);

            OutputSingleMovieScript(name, script, file, ctx);
        }
    }

    private static void WriteFileHeader(TextWriter writer)
    {
    }

    private static void OutputSingleMovieScript(
        string name,
        AstNode.Script script,
        TextWriter writer,
        GlobalContext ctx)
    {
        writer.WriteLine($"Movie script: {name}");

        EmitScriptBody(name, script, writer, ctx, isMovieScript: true);
    }

    private static void EmitScriptBody(
        string name,
        AstNode.Script script,
        TextWriter writer,
        GlobalContext ctx,
        bool isMovieScript)
    {
        var allGlobals = script.Nodes.OfType<AstNode.Global>().SelectMany(g => g.Identifiers)
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        var allHandlers = script.Nodes.OfType<AstNode.Handler>().Select(h => h.Name)
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        var scriptContext = new ScriptContext(ctx, allGlobals, allHandlers, isMovieScript);

        ctx.AllGlobals.UnionWith(allGlobals);

        foreach (var globalType in script.Nodes.OfType<AstNode.TypeSpec>())
        {
            ctx.GlobalTypes.Add(globalType.Name, globalType.Type);
        }

        var props = new HashSet<string>();
        foreach (var prop in script.Nodes.OfType<AstNode.Property>().SelectMany(p => p.Identifiers))
        {
            props.Add(prop);
            writer.WriteLine($"local {prop}");
        }

        var quirks = Config.Quirks.GetValueOrDefault(name);

        foreach (var handler in script.Nodes.OfType<AstNode.Handler>())
        {
            if (quirks?.BlackListHandlers.Contains(handler.Name) ?? false)
                continue;

            // Have to write into a temporary buffer because we need to pre-declare all variables.
            StringWriter tempWriter = new();
            HandlerContext handlerContext = new(scriptContext, handler.Name, tempWriter);
            handlerContext.Locals.UnionWith(handler.Parameters.Select(k => k.Name));
            handlerContext.Locals.UnionWith(props);

            // Writing statements discovers some property of the code we need to know, like type declarations.
            try
            {
                WriteStatementBlock(handler.Body, handlerContext, 1);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to write handler {handler.Name}:\n{e}");
                // writer.WriteLine($"{Indent(2)}throw new System.NotImplementedException(\"Compilation failed\");\n}}");
                continue;
            }

            Dictionary<string, string> types = handlerContext.Types;
            foreach (var param in handler.Parameters)
            {
                if (param.Type is { } type)
                    types.Add(param.Name, type);
            }

            List<AstNode.TypedVariable> paramsList = [.. handler.Parameters];
            if (paramsList.Count > 0 && paramsList[0].Name == "me")
                paramsList.RemoveAt(0);

            // Write function header

            string paramsText = string.Join(", ", paramsList.Select(p => p.Name.ToLower()));
            string handlerLower = WriteSanitizeIdentifier(handler.Name.ToLower());
            string returnType = MapType("return", types);

            writer.WriteLine($"function {handlerLower}({paramsText})");

            // Write local variables

            foreach (var local in handlerContext.DeclaredLocals)
            {
                writer.WriteLine($"{Indent(1)}local {local.ToLower()}");
            }
            writer.WriteLine();

            writer.WriteLine(tempWriter.GetStringBuilder());

            // if (handler.Body.Statements.Length == 0 || handler.Body.Statements[^1] is not AstNode.Return)
                // writer.WriteLine("return");

                // Handler end.
            writer.WriteLine("end");
        }
    }

    private static void WriteStatementBlock(AstNode.StatementBlock node, HandlerContext ctx, int indent)
    {
        foreach (var statement in node.Statements)
        {
            WriteStatement(statement, ctx, indent);
        }
    }

    private static void WriteStatement(AstNode.Base node, HandlerContext ctx, int indent)
    {
        switch (node)
        {
            case AstNode.Assignment ass:
                WriteAssignment(ass, ctx, indent);
                break;
            case AstNode.Return ret:
                WriteReturn(ret, ctx, indent);
                break;
            case AstNode.ExitRepeat exitRepeat:
                WriteExitRepeat(exitRepeat, ctx, indent);
                break;
            case AstNode.NextRepeat nextRepeat:
                WriteNextRepeat(nextRepeat, ctx, indent);
                break;
            case AstNode.Case @case:
                WriteCase(@case, ctx, indent);
                break;
            case AstNode.RepeatWhile repeatWhile:
                WriteRepeatWhile(repeatWhile, ctx, indent);
                break;
            case AstNode.RepeatWithCounter repeatWithCounter:
                WriteRepeatWithCounter(repeatWithCounter, ctx, indent);
                break;
            case AstNode.RepeatWithDownTo repeatWithDownToCounter:
                WriteRepeatWithDownTo(repeatWithDownToCounter, ctx, indent);
                break;
            case AstNode.RepeatWithList repeatWithList:
                WriteRepeatWithList(repeatWithList, ctx, indent);
                break;
            case AstNode.If @if:
                WriteIf(@if, ctx, indent);
                break;
            case AstNode.PutInto putInto:
                WritePutInto(putInto, ctx, indent);
                break;
            case AstNode.Global global:
                WriteGlobal(global, ctx, indent);
                break;
            case AstNode.Property prop:

                break;
            case AstNode.TypeSpec spec:
                MergeTypeSpec(ctx, spec.Name, spec.Type);
                break;

            default:
                if (node is AstNode.VariableName
                    or AstNode.MemberProp or AstNode.MemberIndex or AstNode.BinaryOperator or AstNode.UnaryOperator
                    or AstNode.String or AstNode.Number or AstNode.Symbol or AstNode.List or
                    AstNode.Property)
                {
                    Console.WriteLine($"Warning: {ctx.Name} has loose expression {node}");
                }

                var exprValue = WriteExpression(node, ctx);
                ctx.Writer.Write(Indent(indent));
                ctx.Writer.Write(exprValue);
                ctx.Writer.WriteLine(';');
                break;
        }
    }

    private static string Indent(int indent) => new string(' ', 4 * indent);

    private static void MergeTypeSpec(HandlerContext ctx, string name, string? type)
    {
        if (type == null)
            return;

        ref var curType = ref CollectionsMarshal.GetValueRefOrAddDefault(ctx.Types, name, out _);
        if (curType == null)
        {
            curType = type;
        }
        else if (curType != type)
        {
            Console.WriteLine(
                $"Warning: tried to set local variable '{name}' to conflicting types in handler {ctx.Name}");
        }
    }

    private static void WriteGlobal(AstNode.Global node, HandlerContext ctx, int indent)
    {
        foreach (var declared in node.Identifiers.Except(ctx.Globals))
        {
            // These get handled when locals are declared.
            /*ctx.DeclaredGlobals.Add(declared);
            ctx.Locals.Add(declared);*/
            ctx.Globals.Add(declared);
            ctx.Parent.Parent.AllGlobals.Add(declared);
        }
    }

    private static void WritePutInto(AstNode.PutInto node, HandlerContext ctx, int indent)
    {
        if (node.Type != AstNode.PutType.After)
            throw new NotSupportedException();

        var coll = WriteExpression(node.Collection, ctx);
        var expr = WriteExpression(node.Expression, ctx);
        ctx.Writer.WriteLine($"{Indent(indent)}{coll} = {coll} .. tostring({expr})");
    }

    private static void WriteIf(AstNode.If node, HandlerContext ctx, int indent, bool initialIndent = true)
    {
        var exprParams = new ExpressionParams { WantBool = true };
        var cond = WriteExpression(node.Condition, ctx, exprParams);

        ctx.Writer.WriteLine(exprParams.BoolGranted
            ? $"{Indent(initialIndent ? indent : 0)}if {cond} then"
            : $"{Indent(initialIndent ? indent : 0)}if tobool({cond}) then");

        WriteStatementBlock(node.Statements, ctx, indent + 1);

        if (node.Else != null && node.Else.Statements.Length > 0)
        {
            if (node.Else.Statements.Length == 1 && node.Else.Statements[0] is AstNode.If nextIf)
            {
                // If the else clause is another if it's an else-if chain
                // and we forego the braces around the else.
                ctx.Writer.Write($"{Indent(indent)}else");
                WriteIf(nextIf, ctx, indent, initialIndent: false);
            }
            else
            {
                ctx.Writer.WriteLine($"{Indent(indent)}else\n");
                WriteStatementBlock(node.Else, ctx, indent + 1);
                ctx.Writer.WriteLine($"{Indent(indent)}end");
            }
        }
        else ctx.Writer.WriteLine($"{Indent(indent)}end");
    }

    private static void WriteRepeatWithList(AstNode.RepeatWithList node, HandlerContext ctx, int indent)
    {
        var expr = WriteExpression(node.ListExpr, ctx);
        var name = node.Variable;
        var loopTmp = $"tmp_{name}";
        ctx.Writer.WriteLine($"{Indent(indent)}for _, {loopTmp} in ipairs({expr}) do");

        MakeLoopTmp(ctx, name, loopTmp, number: false, indent + 1);
        WriteStatementBlock(node.Block, ctx, indent + 1);

        ctx.Writer.WriteLine($"{Indent(indent)}end");
    }

    private static void WriteRepeatWithCounter(AstNode.RepeatWithCounter node, HandlerContext ctx, int indent)
    {
        var start = WriteExpression(node.Start, ctx);
        var end = WriteExpression(node.Finish, ctx);
        var name = node.Variable;
        var loopTmp = $"tmp_{name}";

        ctx.Writer.WriteLine($"{Indent(indent)}for {loopTmp} = {start}, {end} + 1 do");

        MakeLoopTmp(ctx, name, loopTmp, number: true, indent + 1);
        WriteStatementBlock(node.Block, ctx, indent + 1);

        ctx.Writer.WriteLine($"{Indent(indent + 1)}{loopTmp} = {WriteVariableNameCore(name, ctx)};");
        ctx.Writer.WriteLine($"{Indent(indent)}end");

        // ctx.LoopTempIdx--;
    }

    private static void WriteRepeatWithDownTo(AstNode.RepeatWithDownTo node, HandlerContext ctx, int indent)
    {
        var start = WriteExpression(node.Start, ctx);
        var end = WriteExpression(node.Finish, ctx);
        var name = node.Variable;
        var loopTmp = $"tmp_{name}";

        ctx.Writer.WriteLine($"{Indent(indent)}for {loopTmp} = {start}, {end}, -1 do");

        MakeLoopTmp(ctx, name, loopTmp, number: true, indent + 1);
        WriteStatementBlock(node.Block, ctx, indent + 1);

        ctx.Writer.WriteLine($"{Indent(indent + 1)}{loopTmp} = {WriteVariableNameCore(name, ctx)};");
        ctx.Writer.WriteLine($"{Indent(indent)}end");
    }

    private static void MakeLoopTmp(HandlerContext ctx, string name, string loopTmp, bool number, int indent)
    {
        if (!IsGlobal(name, ctx, out _) && ctx.Locals.Add(name))
        {
            ctx.DeclaredLocals.Add(name);
            if (number)
                MergeTypeSpec(ctx, name, "number");
        }

        ctx.Writer.WriteLine($"{Indent(indent)}{WriteVariableNameCore(name, ctx)} = {loopTmp};");
    }

    private static void WriteRepeatWhile(AstNode.RepeatWhile node, HandlerContext ctx, int indent)
    {
        var exprParams = new ExpressionParams { WantBool = true };
        var expr = WriteExpression(node.Condition, ctx);

        ctx.Writer.WriteLine(exprParams.BoolGranted
            ? $"{Indent(indent)}while {expr} do"
            : $"{Indent(indent)}while tobool({expr}) do");

        WriteStatementBlock(node.Block, ctx, indent + 1);

        ctx.Writer.WriteLine($"{Indent(indent)}end");
    }

    private static void WriteCase(AstNode.Case node, HandlerContext ctx, int indent)
    {
        var switchExprStr = WriteExpression(node.Expression, ctx);

        for (var c = 0; c < node.Cases.Length; ++c)
        {
            var (exprs, block) = node.Cases[c];

            if (c == 0) ctx.Writer.Write($"{Indent(indent)}if ");
            else ctx.Writer.Write($"{Indent(indent)}elseif ");

            for (var e = 0; e < exprs.Length; ++e)
            {
                var expr = exprs[e];

                if (expr is AstNode.String str)
                    ctx.Writer.Write(switchExprStr + " == " + DoWriteString(str.Value.ToLowerInvariant()));
                else if (expr is AstNode.Number num)
                {
                    ctx.Writer.Write(switchExprStr + " == " + num.Value.ToString());
                }
                else
                    ctx.Writer.Write(switchExprStr + " == " + WriteExpression(expr, ctx));

                if (e < exprs.Length - 1) ctx.Writer.Write(" or ");
            }

            ctx.Writer.WriteLine(" then");

            WriteStatementBlock(block, ctx, indent + 1);
        }

        if (node.Otherwise is not null)
        {
            if (node.Cases.Length == 0)
            {
                WriteStatementBlock(node.Otherwise, ctx, indent + 1);
            }
            else
            {
                ctx.Writer.WriteLine($"{Indent(indent)}else");
                WriteStatementBlock(node.Otherwise, ctx, indent + 1);
            }
        }

        if (node.Cases.Length > 0) ctx.Writer.WriteLine($"{Indent(indent)}end");
    }

    private static void WriteExitRepeat(AstNode.ExitRepeat node, HandlerContext ctx, int indent)
    {
        ctx.Writer.WriteLine($"{Indent(indent)}break;");
    }

    private static void WriteNextRepeat(AstNode.NextRepeat node, HandlerContext ctx, int indent)
    {
        ctx.Writer.WriteLine($"{Indent(indent)}continue;");
    }

    private static void WriteReturn(AstNode.Return ret, HandlerContext ctx, int indent)
    {
        if (ret.Value != null)
        {
            var value = WriteExpression(ret.Value, ctx);
            ctx.Writer.WriteLine($"{Indent(indent)}return {value};");
        }
        else
        {
            ctx.Writer.WriteLine($"{Indent(indent)}return default;");
        }
    }

    private static void WriteAssignment(AstNode.Assignment node, HandlerContext ctx, int indent)
    {
        if (node.Assigned is AstNode.VariableName simpleTarget)
        {
            var name = simpleTarget.Name.ToLower();
            // Define local variable if necessary.
            if (!IsGlobal(name, ctx, out _))
            {
                // Local variable, not global
                // Make sure it's not a parameter though.
                if (ctx.Locals.Add(name))
                    ctx.DeclaredLocals.Add(name);

                MergeTypeSpec(ctx, name, node.Type);
            }
            else if (node.Type != null)
            {
                Console.WriteLine($"Trying to assign-declare type on global: {node.Type}, {node.Assigned}");
            }
        }
        else if (node.Type != null)
        {
            Console.WriteLine($"Unable to infer variable name of assignment type: {node.Type}, {node.Assigned}");
        }

        var lhs = WriteExpression(node.Assigned, ctx);
        var rhs = WriteExpression(node.Value, ctx);

        ctx.Writer.WriteLine($"{Indent(indent)}{lhs} = {rhs}");
    }

    private static string WriteExpression(AstNode.Base node, HandlerContext ctx, ExpressionParams? param = null)
    {
        return node switch
        {
            // Turn pxl member access into a static lookup.
            AstNode.MemberProp
            {
                Property: "image",
                Expression: AstNode.GlobalCall { Name: "member", Arguments: [AstNode.String { Value: "pxl" }] }
            } => "LingoImage.Pxl",
            // Special case concat binary operators for chaining.
            AstNode.BinaryOperator
            {
                Type: AstNode.BinaryOperatorType.Concat or AstNode.BinaryOperatorType.ConcatSpace
            } concatOperator => WriteConcatOperator(concatOperator, ctx),
            AstNode.BinaryOperator binaryOperator => WriteBinaryOperator(binaryOperator, ctx, param),
            AstNode.Constant constant => WriteConstant(constant, ctx),
            AstNode.Number number => WriteNumber(number, ctx),
            AstNode.GlobalCall globalCall => WriteGlobalCall(globalCall, ctx),
            AstNode.List list => WriteList(list, ctx),
            AstNode.MemberCall memberCall => WriteMemberCall(memberCall, ctx),
            AstNode.MemberIndex memberIndex => WriteMemberIndex(memberIndex, ctx),
            AstNode.MemberProp memberProp => WriteMemberProp(memberProp, ctx),
            AstNode.MemberSlice memberSlice => WriteMemberSlice(memberSlice, ctx),
            AstNode.NewCastLib newCastLib => WriteNewCastLib(newCastLib, ctx),
            AstNode.NewScript newScript => WriteNewScript(newScript, ctx),
            AstNode.PropertyList propertyList => WritePropertyList(propertyList, ctx),
            AstNode.String str => WriteString(str, ctx),
            AstNode.Symbol symbol => WriteSymbol(symbol, ctx),
            AstNode.The the => WriteThe(the, ctx),
            AstNode.TheNumberOf theNumberOf => WriteTheNumberOf(theNumberOf, ctx),
            AstNode.TheNumberOfLines theNumberOfLines => WriteTheNumberOfLines(theNumberOfLines, ctx),
            AstNode.ThingOf thingOf => WriteThingOf(thingOf, ctx),
            AstNode.UnaryOperator unaryOperator => WriteUnaryOperator(unaryOperator, ctx, param),
            AstNode.VariableName variableName => WriteVariableName(variableName, ctx),
            _ => throw new NotSupportedException($"{node.GetType()} is not a supported expression type")
        };
    }

    private static string WriteConcatOperator(AstNode.BinaryOperator node, HandlerContext ctx)
    {
        // Flatten recursive chain of concat operators to a straight list.
        var expressions = new List<AstNode.Base> { node.Right };
        var lhs = node.Left;
        while (lhs is AstNode.BinaryOperator binOp && binOp.Type == node.Type)
        {
            expressions.Add(binOp.Right);
            lhs = binOp.Left;
        }

        expressions.Add(lhs);

        expressions.Reverse();

        var func = node.Type switch
        {
            AstNode.BinaryOperatorType.ConcatSpace => " .. ' ' .. ",
            AstNode.BinaryOperatorType.Concat => "..",
            _ => throw new ArgumentOutOfRangeException()
        };

        return string.Join(func, expressions.Select(e => WriteExpression(e, ctx)));

        // return WriteGlobalCall(func, ctx, expressions.ToArray());
    }

    private static string WriteThingOf(AstNode.ThingOf thingOf, HandlerContext ctx)
    {
        var helper = thingOf.Type switch
        {
            AstNode.ThingOfType.Item => "itemof_helper",
            AstNode.ThingOfType.Line => "lineof_helper",
            AstNode.ThingOfType.Char => "charof_helper",
            _ => throw new ArgumentOutOfRangeException()
        };

        return WriteGlobalCall(helper, ctx, thingOf.Index, thingOf.Collection);
    }

    private static string WriteTheNumberOfLines(AstNode.TheNumberOfLines node, HandlerContext ctx)
    {
        return WriteGlobalCall("thenumberoflines_helper", ctx, node.Text);
    }

    private static string WriteTheNumberOf(AstNode.TheNumberOf node, HandlerContext ctx)
    {
        return WriteGlobalCall("thenumberof_helper", ctx, node.Expr);
    }

    private static string WriteThe(AstNode.The node, HandlerContext ctx)
    {
        return $"_global.the_{node.Name}";
    }

    private static string WritePropertyList(AstNode.PropertyList node, HandlerContext ctx)
    {
        if (node.Values.Length == 0)
            return "{}";

        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var (k, v) in node.Values)
        {
            if (!first)
                sb.Append(", ");
            first = false;

            // var kExpr = WriteExpression(k, ctx);
            var kExpr = k switch
            {
                AstNode.String str => str.Value,
                AstNode.Symbol sym => sym.Value,
                _ => WriteExpression(k, ctx),
            };
            var vExpr = WriteExpression(v, ctx);

            // sb.Append('[');
            sb.Append(kExpr);
            sb.Append(" = ");
            sb.Append(vExpr);
        }

        sb.Append('}');

        return sb.ToString();
    }

    private static string WriteVariableName(AstNode.VariableName variableName, HandlerContext ctx)
    {
        return WriteVariableNameCore(variableName.Name, ctx);
    }

    private static string WriteVariableNameCore(string name, HandlerContext ctx)
    {
        var lowered = name.ToLower();

        // if (lowered == "me")
        //     return "this";

        if (ctx.Locals.Contains(lowered))
            return $"{lowered}";

        if (IsGlobal(name, ctx, out var casedName))
            // return $"Glob.{casedName}";
            return $"{casedName}";

        return $"{name}";
    }

    private static bool IsGlobal(string name, HandlerContext ctx, [NotNullWhen(true)] out string? caseName)
    {
        if (!ctx.Parent.AllGlobals.Contains(name) && !ctx.Globals.Contains(name))
        {
            caseName = null;
            return false;
        }

        if (!ctx.Parent.Parent.AllGlobals.TryGetValue(name, out caseName))
            throw new InvalidOperationException("Global declared but not in all globals list?");

        return true;
    }

    private static string WriteUnaryOperator(
        AstNode.UnaryOperator unaryOperator,
        HandlerContext ctx,
        ExpressionParams? exprParams)
    {
        if (unaryOperator.Type == AstNode.UnaryOperatorType.Not)
        {
            var subParams = new ExpressionParams { WantBool = true };
            var expr = WriteExpression(unaryOperator.Expression, ctx, subParams);

            var sb = new StringBuilder();

            sb.Append(expr);

            if (!subParams.BoolGranted)
            {
                sb.Insert(0, "tobool(");
                sb.Append(')');
            }

            sb.Insert(0, "not ");

            if (exprParams is not { WantBool: true })
            {
                sb.Insert(0, "toint(");
                sb.Append(')');
            }
            else
            {
                exprParams.BoolGranted = true;
            }


            return sb.ToString();
        }
        else
        {
            Debug.Assert(unaryOperator.Type == AstNode.UnaryOperatorType.Negate);
            var expr = WriteExpression(unaryOperator.Expression, ctx);

            return $"-{expr}";
        }
    }

    private static string WriteSymbol(AstNode.Symbol node, HandlerContext ctx)
    {
        return $"\"{node.Value}\"";
    }

    private static string WriteString(AstNode.String node, HandlerContext ctx)
    {
        return DoWriteString(node.Value);
    }

    private static string DoWriteString(string str)
    {
        var escaped = str.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string WriteNewScript(AstNode.NewScript node, HandlerContext ctx)
    {
        var wrapListNode = new AstNode.List(node.Args);
        return WriteGlobalCall("new_script", ctx, node.Type, wrapListNode);
    }

    private static string WriteNewCastLib(AstNode.NewCastLib node, HandlerContext ctx)
    {
        return WriteGlobalCall("new_castlib", ctx, node.Type, node.CastLib);
    }

    private static string WriteMemberSlice(AstNode.MemberSlice node, HandlerContext ctx)
    {
        return WriteGlobalCall("slice_helper", ctx, node.Expression, node.Start, node.End);
    }

    private static string WriteMemberProp(AstNode.MemberProp node, HandlerContext ctx)
    {
        if (node.Property == "char")
            return WriteGlobalCall("charmember_helper", ctx, node.Expression);
        if (node.Property == "line")
            return WriteGlobalCall("linemember_helper", ctx, node.Expression);
        if (node.Property == "length")
            return WriteGlobalCall("lengthmember_helper", ctx, node.Expression);

        var child = WriteExpression(node.Expression, ctx);
        return $"{child}.{WriteSanitizeIdentifier(node.Property.ToLower())}";
    }

    private static string WriteMemberIndex(AstNode.MemberIndex node, HandlerContext ctx)
    {
        var child = WriteExpression(node.Expression, ctx);
        var idx = WriteExpression(node.Index, ctx);
        return $"{child}[{idx}]";
    }

    private static string WriteMemberCall(AstNode.MemberCall node, HandlerContext ctx)
    {
        var child = WriteExpression(node.Expression, ctx);
        var args = node.Parameters.Select(v => WriteExpression(v, ctx));
        var name = WriteSanitizeIdentifier(node.Name.ToLower());
        return $"{child}.{name}({string.Join(", ", args)})";
    }

    private static string WriteList(AstNode.List node, HandlerContext ctx)
    {
        if (node.Values.Length == 0)
            return "{}";

        var args = node.Values.Select(v => WriteExpression(v, ctx));
        return $"{{ {string.Join(", ", args)} }}";
    }

    private static string MovieScriptPrefix(HandlerContext ctx)
    {
        return ctx.Parent.IsMovieScript ? "" : "_movieScript.";
    }

    private static string WriteNumber(AstNode.Number node, HandlerContext ctx)
    {
        return $"{node.Value}";
    }

    private static string WriteConstant(AstNode.Constant node, HandlerContext ctx)
    {
        if (node.Name.Equals("void", StringComparison.InvariantCultureIgnoreCase))
            return "nil";

        // return $"Glob.{node.Name.ToUpper()}";
        return $"{node.Name.ToUpper()}";
    }

    private static string WriteBinaryOperator(
        AstNode.BinaryOperator node,
        HandlerContext ctx,
        ExpressionParams? param)
    {
        if (param?.WantBool ?? false)
        {
            if (node.Type is AstNode.BinaryOperatorType.Or or AstNode.BinaryOperatorType.And or AstNode
                    .BinaryOperatorType.Sor or AstNode.BinaryOperatorType.Sand)
            {
                return WriteBinaryBoolOp(node, ctx, param);
            }

            if (node.Type is >= AstNode.BinaryOperatorType.LessThan and <= AstNode.BinaryOperatorType
                    .GreaterThanOrEqual)
            {
                return WriteComparisonBoolOp(node, ctx, param);
            }
        }

        // Operators that need to map to special functions.
        var helperOps = node.Type switch
        {
            AstNode.BinaryOperatorType.Contains => "contains",
            AstNode.BinaryOperatorType.Starts => "starts",
            AstNode.BinaryOperatorType.LessThan => "op_lt",
            AstNode.BinaryOperatorType.LessThanOrEqual => "op_le",
            AstNode.BinaryOperatorType.NotEqual => "op_ne",
            AstNode.BinaryOperatorType.Equal => "op_eq",
            AstNode.BinaryOperatorType.GreaterThan => "op_gt",
            AstNode.BinaryOperatorType.GreaterThanOrEqual => "op_ge",
            AstNode.BinaryOperatorType.And => "op_and",
            AstNode.BinaryOperatorType.Or => "op_or",
            AstNode.BinaryOperatorType.Sand => "op_sand",
            AstNode.BinaryOperatorType.Sor => "op_sor",
            AstNode.BinaryOperatorType.Add => "op_add",
            AstNode.BinaryOperatorType.Subtract => "op_sub",
            AstNode.BinaryOperatorType.Multiply => "op_mul",
            AstNode.BinaryOperatorType.Divide => "op_div",
            AstNode.BinaryOperatorType.Mod => "op_mod",
            _ => throw new ArgumentOutOfRangeException()
        };

        string exprTemplate = $"{WriteExpression(node.Left, ctx, param)} {{0}} {WriteExpression(node.Right, ctx, param)}";

        switch (node.Type)
        {
            case AstNode.BinaryOperatorType.LessThan:
                return string.Format(exprTemplate, "<");

            case AstNode.BinaryOperatorType.LessThanOrEqual:
                return string.Format(exprTemplate, "<=");

            case AstNode.BinaryOperatorType.NotEqual:
                return string.Format(exprTemplate, "~=");

            case AstNode.BinaryOperatorType.Equal:
                return string.Format(exprTemplate, "==");

            case AstNode.BinaryOperatorType.GreaterThan:
                return string.Format(exprTemplate, ">");

            case AstNode.BinaryOperatorType.GreaterThanOrEqual:
                return string.Format(exprTemplate, ">=");

            case AstNode.BinaryOperatorType.And:
                return string.Format(exprTemplate, "and");

            case AstNode.BinaryOperatorType.Or:
                return string.Format(exprTemplate, "or");

            case AstNode.BinaryOperatorType.Sand:
                return string.Format(exprTemplate, "and");

            case AstNode.BinaryOperatorType.Sor:
                return string.Format(exprTemplate, "or");

            case AstNode.BinaryOperatorType.Add:
                return string.Format(exprTemplate, "+");

            case AstNode.BinaryOperatorType.Subtract:
                return string.Format(exprTemplate, "-");

            case AstNode.BinaryOperatorType.Multiply:
                return string.Format(exprTemplate, "*");

            case AstNode.BinaryOperatorType.Divide:
                return string.Format(exprTemplate, "/");

            case AstNode.BinaryOperatorType.Mod:
                return string.Format(exprTemplate, "mod");
        }

        return WriteGlobalCall(helperOps, ctx, node.Left, node.Right);
    }

    private static string WriteComparisonBoolOp(
        AstNode.BinaryOperator node,
        HandlerContext ctx,
        ExpressionParams expressionParams)
    {
        var exprLeft = WriteExpression(node.Left, ctx);
        var exprRight = WriteExpression(node.Right, ctx);

        expressionParams.BoolGranted = true;

        return node.Type switch
        {
            // Use non-short-circuiting ops.
            AstNode.BinaryOperatorType.LessThan => $"{exprLeft} < {exprRight}",
            AstNode.BinaryOperatorType.LessThanOrEqual => $"{exprLeft} <= {exprRight}",
            AstNode.BinaryOperatorType.GreaterThanOrEqual => $"{exprLeft} >= {exprRight}",
            AstNode.BinaryOperatorType.GreaterThan => $"{exprLeft} > {exprRight}",
            AstNode.BinaryOperatorType.Equal => $"{exprLeft} == {exprRight}",
            AstNode.BinaryOperatorType.NotEqual => $"{exprLeft} ~= {exprRight}",
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static string WriteBinaryBoolOp(
        AstNode.BinaryOperator node,
        HandlerContext ctx,
        ExpressionParams expressionParams)
    {
        var exprParamLeft = new ExpressionParams { WantBool = true };
        var exprParamRight = new ExpressionParams { WantBool = true };

        var exprLeft = WriteExpression(node.Left, ctx, exprParamLeft);
        var exprRight = WriteExpression(node.Right, ctx, exprParamRight);

        if (!exprParamLeft.BoolGranted)
            exprLeft = $"tobool({exprLeft})";

        if (!exprParamRight.BoolGranted)
            exprRight = $"tobool({exprRight})";

        var op = node.Type switch
        {
            AstNode.BinaryOperatorType.And => "and",
            AstNode.BinaryOperatorType.Or => "or",
            AstNode.BinaryOperatorType.Sand => "and",
            AstNode.BinaryOperatorType.Sor => "and",
            _ => throw new ArgumentOutOfRangeException()
        };

        expressionParams.BoolGranted = true;

        return $"({exprLeft} {op} {exprRight})";
    }

    private static string WriteGlobalCall(AstNode.GlobalCall node, HandlerContext ctx)
    {
        var args = node.Arguments.Select(a => WriteExpression(a, ctx));
        var lower = node.Name.ToLower();
        if (ctx.Parent.AllHandlers.Contains(lower))
        {
            // Local call
            return $"{lower}({string.Join(',', args)})";
        }

        if (ctx.Parent.Parent.MovieHandlers.Contains(node.Name))
        {
            // Movie script call
            return $"{MovieScriptPrefix(ctx)}.{lower}({string.Join(',', args)})";
        }

        return WriteGlobalCall(lower, ctx, args);
    }

    private static string WriteGlobalCall(string name, HandlerContext ctx, params AstNode.Base[] args)
    {
        return WriteGlobalCall(name, ctx, args.Select(a => WriteExpression(a, ctx)));
    }

    private static string WriteGlobalCall(string name, HandlerContext ctx, IEnumerable<string> args)
    {
        // var method = typeof(LingoGlobal).GetMember(name,
        //     BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

        // var isStatic = method.Any(m => m is MethodInfo { IsStatic: true });

        // TODO: Generalize
        if (name is "image" or "color" or "rect" or "point" or "random")
        {
            return $"{name}({string.Join(", ", args)})";
        }

        if (name is "string")
        {
            return $"tostring({string.Join(", ", args)})";
        }

        var sb = new StringBuilder();

        // sb.Append(isStatic ? "LingoGlobal." : "_global.");
        // sb.Append("Glob.");
        sb.Append(WriteSanitizeIdentifier(name.ToLower()));
        sb.Append('(');
        sb.Append(string.Join(", ", args));
        sb.Append(')');

        return sb.ToString();
    }

    private static string WriteSanitizeIdentifier(string identifier)
    {
        return LuaPreservedKeywords.Contains(identifier) ? $"@{identifier}" : identifier;
    }

    private static string MapType(string variable, Dictionary<string, string> types)
    {
        var type = types.GetValueOrDefault(variable, "dynamic");
        return Config.TypeKeywords.GetValueOrDefault(type, type);
    }
}
