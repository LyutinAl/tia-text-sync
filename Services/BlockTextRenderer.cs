using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaTextSync.Services;

/// <summary>
/// Строит компактный текстовый вид блока LAD/FBD из его SimaticML XML.
/// Вид предназначен только для чтения и diff: логика каждой сети записывается
/// выражениями, а импорт по-прежнему использует XML.
/// </summary>
internal static class BlockTextRenderer
{
    private const string BlockElementPrefix = "SW.Blocks.";
    private const int    MaxLineLength      = 120;

    /// <summary>Возвращает текстовый вид или null, если язык блока не LAD/FBD.</summary>
    public static (string Extension, string Text)? Render(string xmlPath)
    {
        var block = XDocument.Load(xmlPath).Root?.Elements()
            .FirstOrDefault(e => e.Name.LocalName.StartsWith(BlockElementPrefix, StringComparison.Ordinal));
        if (block == null)
            return null;

        var attributes = block.Child("AttributeList");
        var language   = attributes?.Child("ProgrammingLanguage")?.Value ?? "";
        var extension  = language.Contains("LAD") ? "lad" : language.Contains("FBD") ? "fbd" : null;
        if (extension == null)
            return null;

        var text = new StringBuilder();
        text.Append("// Read-only view of the block XML. Changes made here are not imported.\n");
        text.Append($"{block.Name.LocalName.Substring(BlockElementPrefix.Length)} ");
        text.Append($"\"{attributes?.Child("Name")?.Value}\" [{attributes?.Child("Number")?.Value}] {language}\n");
        AppendComment(text, MultilingualText(block, "Comment"));

        AppendInterface(text, attributes?.Child("Interface"));

        var number = 0;
        foreach (var unit in block.Child("ObjectList").Children("SW.Blocks.CompileUnit"))
            AppendNetwork(text, unit, ++number);

        return (extension, text.ToString());
    }

    // ── Интерфейс ────────────────────────────────────────────────────────────

    private static void AppendInterface(StringBuilder text, XElement? blockInterface)
    {
        foreach (var section in blockInterface.Child("Sections").Children("Section"))
        {
            var members = section.Children("Member")
                .Where(m => m.Attr("Datatype") != "Void")
                .ToList();
            if (members.Count == 0)
                continue;

            text.Append($"\n{section.Attr("Name").ToUpperInvariant()}\n");
            foreach (var member in members)
                AppendMember(text, member, "  ");
        }
    }

    private static void AppendMember(StringBuilder text, XElement member, string indent)
    {
        text.Append($"{indent}{member.Attr("Name")} : {member.Attr("Datatype")}");

        var startValue = member.Child("StartValue")?.Value;
        if (!string.IsNullOrEmpty(startValue))
            text.Append($" := {startValue}");

        var comment = member.Child("Comment").Children("MultiLanguageText")
            .Select(t => t.Value)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (comment != null)
            text.Append($"  // {SingleLine(comment)}");

        text.Append('\n');

        foreach (var child in member.Children("Member"))
            AppendMember(text, child, indent + "  ");
    }

    // ── Сети ─────────────────────────────────────────────────────────────────

    private static void AppendNetwork(StringBuilder text, XElement unit, int number)
    {
        var title = MultilingualText(unit, "Title");
        text.Append(string.IsNullOrEmpty(title)
            ? $"\nNETWORK {number}\n"
            : $"\nNETWORK {number}: {SingleLine(title!)}\n");
        AppendComment(text, MultilingualText(unit, "Comment"));

        var attributes = unit.Child("AttributeList");
        var source     = attributes.Child("NetworkSource");
        var network    = source.Child("FlgNet");
        if (network != null)
        {
            foreach (var statement in new Network(network).Render())
                text.Append(BreakLongStatement(statement)).Append('\n');
        }
        else if (source?.HasElements == true)
        {
            text.Append($"// {attributes.Child("ProgrammingLanguage")?.Value} network: see the XML file\n");
        }
    }

    /// <summary>Разбивает длинный оператор по ветвям OR и аргументам, чтобы diff показывал только изменившуюся ветвь.</summary>
    private static string BreakLongStatement(string statement)
    {
        if (statement.Length <= MaxLineLength)
            return statement;

        var result = new StringBuilder();
        var depth  = 0;
        var quoted = false;
        for (var i = 0; i < statement.Length; i++)
        {
            var c = statement[i];
            if (c == '"')
                quoted = !quoted;
            else if (!quoted && c == '(')
                depth++;
            else if (!quoted && c == ')')
                depth--;

            if (!quoted && string.CompareOrdinal(statement, i, " OR ", 0, 4) == 0)
            {
                result.Append(NewLine(depth));
            }
            else if (!quoted && depth > 0 && string.CompareOrdinal(statement, i, ", ", 0, 2) == 0)
            {
                result.Append(',').Append(NewLine(depth));
                i++;
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    private static string NewLine(int depth) =>
        "\n" + new string(' ', 2 + 2 * depth);

    private static void AppendComment(StringBuilder text, string? comment)
    {
        if (string.IsNullOrEmpty(comment))
            return;

        foreach (var line in comment!.Split('\n'))
            text.Append($"// {line.TrimEnd('\r')}\n");
    }

    private static string? MultilingualText(XElement owner, string compositionName) =>
        owner.Child("ObjectList").Children("MultilingualText")
            .Where(t => t.Attr("CompositionName") == compositionName)
            .SelectMany(t => t.Child("ObjectList").Children("MultilingualTextItem"))
            .Select(i => i.Child("AttributeList")?.Child("Text")?.Value)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    private static string SingleLine(string text) =>
        text.Replace("\r", "").Replace('\n', ' ').Trim();

    // ── Обход XML без учёта пространств имён ─────────────────────────────────

    private static XElement? Child(this XElement? element, string name) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(this XElement? element, string name) =>
        element == null
            ? Enumerable.Empty<XElement>()
            : element.Elements().Where(e => e.Name.LocalName == name);

    private static string Attr(this XElement element, string name) =>
        element.Attribute(name)?.Value ?? "";

    // ── Выражения ────────────────────────────────────────────────────────────

    private readonly struct Expr
    {
        public const int Or = 1, Xor = 2, And = 3, Compare = 4, Not = 5, Atom = 6;

        public readonly string Text;
        public readonly int    Precedence;

        private readonly IReadOnlyList<Expr>? _factors;

        public Expr(string text, int precedence = Atom, IReadOnlyList<Expr>? factors = null)
        {
            Text       = text;
            Precedence = precedence;
            _factors   = factors;
        }

        public bool IsTrue => Text == "TRUE";

        /// <summary>Сомножители AND-цепочки; для остальных выражений — само выражение.</summary>
        public IReadOnlyList<Expr> Factors => _factors ?? new[] { this };

        public string Wrap(int minPrecedence) =>
            Precedence >= minPrecedence ? Text : $"({Text})";
    }

    private static readonly Expr True    = new("TRUE");
    private static readonly Expr Unknown = new("?");

    private static Expr MakeAnd(Expr left, Expr right) =>
        JoinFactors(left.Factors.Concat(right.Factors).ToList());

    private static Expr JoinFactors(IReadOnlyList<Expr> factors)
    {
        var significant = factors.Where(f => !f.IsTrue).ToList();
        return significant.Count switch
        {
            0 => True,
            1 => significant[0],
            _ => new Expr(string.Join(" AND ", significant.Select(f => f.Wrap(Expr.And))), Expr.And, significant)
        };
    }

    private static Expr MakeOr(IReadOnlyList<Expr> items)
    {
        if (items.Count == 0)         return Unknown;
        if (items.Count == 1)         return items[0];
        if (items.Any(i => i.IsTrue)) return True;

        // Общее начало параллельных ветвей выносится за скобки: (a AND b) OR (a AND c) = a AND (b OR c)
        var branches = items.Select(i => i.Factors).ToList();
        var shared   = 0;
        while (branches.All(b => b.Count > shared && b[shared].Text == branches[0][shared].Text))
            shared++;

        if (shared > 0)
        {
            var rest = branches.Select(b => JoinFactors(b.Skip(shared).ToList())).ToList();
            return MakeAnd(JoinFactors(branches[0].Take(shared).ToList()), MakeOr(rest));
        }

        // И-цепочки внутри ИЛИ берутся в скобки: так проще читать параллельные ветви
        var parts = items.Select(i => i.Precedence == Expr.Or ? i.Text : i.Wrap(Expr.Compare));
        return new Expr(string.Join(" OR ", parts), Expr.Or);
    }

    private static Expr MakeXor(IEnumerable<Expr> items) =>
        new(string.Join(" XOR ", items.Select(i => i.Wrap(Expr.Compare))), Expr.Xor);

    private static Expr MakeNot(Expr value) =>
        new($"NOT {value.Wrap(Expr.Not)}", Expr.Not);

    // ── Операнды ─────────────────────────────────────────────────────────────

    private static string AccessText(XElement access)
    {
        var scope = access.Attr("Scope");

        var constant = access.Child("Constant");
        if (constant != null)
        {
            var name = constant.Attr("Name");
            return name.Length > 0
                ? RootName(name, scope)
                : constant.Child("ConstantValue")?.Value ?? "?";
        }

        var symbol = access.Child("Symbol");
        if (symbol != null)
            return SymbolText(symbol, scope);

        var address = access.Child("Address");
        return address != null ? $"%{address.Attr("Area")}@{address.Attr("BitOffset")}" : "?";
    }

    private static string SymbolText(XElement owner, string scope)
    {
        var text  = new StringBuilder();
        var first = true;
        foreach (var component in owner.Children("Component"))
        {
            var name = component.Attr("Name");
            text.Append(first ? RootName(name, scope) : $".{Quoted(name)}");
            first = false;

            var indexes = component.Children("Access").Select(AccessText).ToList();
            if (indexes.Count > 0)
                text.Append($"[{string.Join(", ", indexes)}]");

            var slice = component.Attr("SliceAccessModifier");
            if (slice.Length > 0)
                text.Append($".%{slice.ToUpperInvariant()}");
        }
        return text.ToString();
    }

    private static string RootName(string name, string scope) =>
        scope.StartsWith("Local", StringComparison.Ordinal) ? $"#{name}" : $"\"{name}\"";

    private static string Quoted(string name) =>
        Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") ? name : $"\"{name}\"";

    // ── Одна сеть FlgNet ─────────────────────────────────────────────────────

    /// <summary>
    /// Разбирает граф сети: каждый провод связывает источник (шину, операнд или выход элемента)
    /// с входами других элементов. Операторы строятся по катушкам и боксам, условие каждого
    /// собирается обходом назад по проводам.
    /// </summary>
    private sealed class Network
    {
        private static readonly HashSet<string> Coils = new() { "Coil", "SCoil", "RCoil" };

        // Элементы, которые только вычисляют условие и не образуют своего оператора
        private static readonly HashSet<string> LogicParts = new()
        {
            "Contact", "PContact", "NContact", "PBox", "NBox", "O", "A", "X", "Not",
            "Eq", "Ne", "Gt", "Ge", "Lt", "Le"
        };

        private static readonly Dictionary<string, string> Comparisons = new()
        {
            ["Eq"] = "=", ["Ne"] = "<>", ["Gt"] = ">", ["Ge"] = ">=", ["Lt"] = "<", ["Le"] = "<="
        };

        private static readonly Dictionary<string, string> Arithmetic = new()
        {
            ["Add"] = "+", ["Sub"] = "-", ["Mul"] = "*", ["Div"] = "/", ["Mod"] = "MOD"
        };

        private readonly List<XElement>                     _parts    = new();
        private readonly Dictionary<string, XElement>       _partById = new();
        private readonly Dictionary<string, int>            _ordinals = new();
        private readonly Dictionary<string, string>         _accesses = new();

        // Источник входа: (null, null) — шина питания, (uid, null) — операнд, (uid, pin) — выход элемента
        private readonly Dictionary<(string Uid, string Pin), (string? Uid, string? Pin)> _drivers = new();
        private readonly Dictionary<string, List<string>>                 _inputPins = new();
        private readonly Dictionary<string, List<(string Pin, string Target)>> _targets = new();
        private readonly HashSet<string>                                  _referenced = new();

        private readonly Dictionary<(string Uid, string Pin), Expr> _cache    = new();
        private readonly HashSet<(string Uid, string Pin)>          _visiting = new();
        private readonly HashSet<string>                            _emitted  = new();
        private readonly List<string>                               _statements = new();

        public Network(XElement network)
        {
            foreach (var element in network.Child("Parts")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                var uid = element.Attr("UId");
                if (element.Name.LocalName == "Access")
                {
                    _accesses[uid] = AccessText(element);
                }
                else
                {
                    _parts.Add(element);
                    _partById[uid] = element;
                    if (!IsLogic(element) && !IsCoil(element))
                        _ordinals[uid] = _ordinals.Count + 1;
                }
            }

            foreach (var wire in network.Child("Wires")?.Elements() ?? Enumerable.Empty<XElement>())
                ReadWire(wire.Elements().ToList());
        }

        private void ReadWire(List<XElement> connections)
        {
            if (connections.Count < 2)
                return;

            (string? Uid, string? Pin) source;
            var first = connections[0];
            switch (first.Name.LocalName)
            {
                case "Powerrail": source = (null, null);                             break;
                case "IdentCon":  source = (first.Attr("UId"), null);                break;
                case "NameCon":   source = (first.Attr("UId"), first.Attr("Name"));  break;
                default:          return;  // Неподключённый вход
            }

            foreach (var sink in connections.Skip(1))
            {
                var uid = sink.Attr("UId");
                switch (sink.Name.LocalName)
                {
                    case "NameCon":
                        var pin = sink.Attr("Name");
                        _drivers[(uid, pin)] = source;
                        GetOrAdd(_inputPins, uid).Add(pin);
                        if (source.Pin != null)
                            _referenced.Add(source.Uid!);
                        break;

                    case "IdentCon" when source.Pin != null:
                        GetOrAdd(_targets, source.Uid!).Add((source.Pin, _accesses.TryGetValue(uid, out var t) ? t : "?"));
                        break;
                }
            }
        }

        private static List<T> GetOrAdd<T>(Dictionary<string, List<T>> map, string key)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<T>();
            return list;
        }

        public IReadOnlyList<string> Render()
        {
            foreach (var part in _parts.Where(p => !IsLogic(p)))
                Emit(part);
            return _statements;
        }

        private static bool IsLogic(XElement part) =>
            part.Name.LocalName == "Part" && LogicParts.Contains(part.Attr("Name"));

        private static bool IsCoil(XElement part) =>
            part.Name.LocalName == "Part" && Coils.Contains(part.Attr("Name"));

        private void Emit(XElement part)
        {
            // Оператор добавляется после обхода его входов, поэтому боксы-источники идут раньше
            if (_emitted.Add(part.Attr("UId")))
                _statements.Add(BuildStatement(part));
        }

        // ── Операторы ────────────────────────────────────────────────────────

        private string BuildStatement(XElement part)
        {
            if (IsCoil(part))
            {
                var operand   = Pin(part, "operand").Text;
                var condition = Pin(part, "in");
                return part.Attr("Name") switch
                {
                    "SCoil" => WithCondition($"SET {operand}", condition),
                    "RCoil" => WithCondition($"RESET {operand}", condition),
                    _       => $"{operand} := {condition.Text}"
                };
            }

            var enable = OptionalPin(part, "en");
            var tag    = NeedsTag(part) ? $"[{BoxLabel(part)}] " : "";
            return tag + WithCondition(SimpleForm(part) ?? CallForm(part), enable);
        }

        private static string WithCondition(string statement, Expr condition) =>
            condition.IsTrue ? statement : $"{statement} IF {condition.Text}";

        /// <summary>Запись присваиванием для MOVE, арифметики и CALCULATE, когда выходы — только операнды.</summary>
        private string? SimpleForm(XElement part)
        {
            if (part.Name.LocalName != "Part")
                return null;

            var uid     = part.Attr("UId");
            var name    = part.Attr("Name");
            var targets = _targets.TryGetValue(uid, out var list)
                ? list
                : new List<(string Pin, string Target)>();

            var outputsOnly = targets.Count > 0 && !_referenced.Contains(uid);

            if (name == "Move" && outputsOnly && targets.All(t => t.Pin.StartsWith("out", StringComparison.Ordinal)))
                return $"{string.Join(", ", targets.Select(t => t.Target))} := {Pin(part, "in").Text}";

            if (!outputsOnly || targets.Count != 1 || targets[0].Pin != "out")
                return null;

            if (Arithmetic.TryGetValue(name, out var op))
            {
                var operands = NumberedInputs(uid).Select(p => Pin(part, p).Wrap(Expr.Atom));
                return $"{targets[0].Target} := {string.Join($" {op} ", operands)}";
            }

            if (name == "Calc" && part.Child("Equation") is { } equation)
            {
                var value = Regex.Replace(equation.Value, @"\bIN(\d+)\b",
                    m => Pin(part, $"in{m.Groups[1].Value}").Text, RegexOptions.IgnoreCase);
                return $"{targets[0].Target} := {value}";
            }

            return null;
        }

        /// <summary>Общая запись: Имя @ Экземпляр(вход := значение, выход => операнд).</summary>
        private string CallForm(XElement part)
        {
            var uid  = part.Attr("UId");
            var args = new List<string>();

            if (_inputPins.TryGetValue(uid, out var inputs))
                args.AddRange(inputs.Where(p => p != "en").Select(p => $"{p} := {Pin(part, p).Text}"));

            if (_targets.TryGetValue(uid, out var targets))
                args.AddRange(targets.Select(t => $"{t.Pin} => {t.Target}"));

            var instance = InstanceOf(part);
            var call     = instance == null
                ? BoxName(part)
                : $"{BoxName(part)} @ {SymbolText(instance, instance.Attr("Scope"))}";
            return $"{call}({string.Join(", ", args)})";
        }

        private static string BoxName(XElement part)
        {
            if (part.Name.LocalName != "Part")
                return $"\"{part.Child("CallInfo")?.Attr("Name")}\"";

            var types = part.Children("TemplateValue")
                .Where(t => t.Attr("Type") == "Type")
                .Select(t => t.Value)
                .ToList();
            return types.Count > 0
                ? $"{part.Attr("Name")}<{string.Join(",", types)}>"
                : part.Attr("Name");
        }

        private static XElement? InstanceOf(XElement part) =>
            part.Child("Instance") ?? part.Child("CallInfo").Child("Instance");

        private bool NeedsTag(XElement part) =>
            _referenced.Contains(part.Attr("UId")) && InstanceOf(part) == null;

        private string BoxLabel(XElement part)
        {
            var name = part.Name.LocalName == "Part" ? part.Attr("Name") : part.Child("CallInfo")?.Attr("Name");
            return $"{name}#{_ordinals[part.Attr("UId")]}";
        }

        // ── Условия ──────────────────────────────────────────────────────────

        private Expr OptionalPin(XElement part, string pin) =>
            _drivers.ContainsKey((part.Attr("UId"), pin)) ? Pin(part, pin) : True;

        private Expr Pin(XElement part, string pin)
        {
            if (!_drivers.TryGetValue((part.Attr("UId"), pin), out var source))
                return Unknown;

            var value = SourceExpr(source);
            return IsNegated(part, pin) ? MakeNot(value) : value;
        }

        private static bool IsNegated(XElement part, string pin) =>
            part.Children("Negated").Any(n => n.Attr("Name") == pin);

        private Expr SourceExpr((string? Uid, string? Pin) source)
        {
            if (source.Uid == null)
                return True;
            if (source.Pin == null)
                return _accesses.TryGetValue(source.Uid, out var text) ? new Expr(text) : Unknown;
            return Output(source.Uid, source.Pin);
        }

        private Expr Output(string uid, string pin)
        {
            var key = (uid, pin);
            if (_cache.TryGetValue(key, out var cached))
                return cached;
            if (!_partById.TryGetValue(uid, out var part) || !_visiting.Add(key))
                return Unknown;

            var value = ComputeOutput(part, pin);
            if (IsNegated(part, pin))
                value = MakeNot(value);

            _visiting.Remove(key);
            _cache[key] = value;
            return value;
        }

        private Expr ComputeOutput(XElement part, string pin)
        {
            var name = part.Name.LocalName == "Part" ? part.Attr("Name") : "";
            switch (name)
            {
                case "Contact":  return MakeAnd(Pin(part, "in"), Pin(part, "operand"));
                case "PContact": return MakeAnd(OptionalPin(part, "pre"), Edge("P", Pin(part, "operand"), part));
                case "NContact": return MakeAnd(OptionalPin(part, "pre"), Edge("N", Pin(part, "operand"), part));
                case "PBox":     return Edge("P", Pin(part, "in"), part);
                case "NBox":     return Edge("N", Pin(part, "in"), part);
                case "Not":      return MakeNot(Pin(part, "in"));
                case "O":        return MakeOr(NumberedInputs(part).ToList());
                case "A":        return NumberedInputs(part).Aggregate(True, MakeAnd);
                case "X":        return MakeXor(NumberedInputs(part));
            }

            if (Comparisons.TryGetValue(name, out var op))
            {
                var comparison = new Expr($"{Pin(part, "in1").Text} {op} {Pin(part, "in2").Text}", Expr.Compare);
                return MakeAnd(OptionalPin(part, "pre"), comparison);
            }

            Emit(part);
            return IsCoil(part) ? Pin(part, "in") : new Expr(OutputName(part, pin));
        }

        private Expr Edge(string kind, Expr signal, XElement part) =>
            new($"{kind}({signal.Text}, {Pin(part, "bit").Text})");

        private string OutputName(XElement part, string pin)
        {
            var instance = InstanceOf(part);
            var owner    = instance != null ? SymbolText(instance, instance.Attr("Scope")) : BoxLabel(part);
            return $"{owner}.{pin}";
        }

        private IEnumerable<Expr> NumberedInputs(XElement part) =>
            NumberedInputs(part.Attr("UId")).Select(p => Pin(part, p));

        private IEnumerable<string> NumberedInputs(string uid) =>
            !_inputPins.TryGetValue(uid, out var pins)
                ? Enumerable.Empty<string>()
                : pins.Where(p => Regex.IsMatch(p, @"^in\d+$"))
                      .OrderBy(p => int.Parse(p.Substring(2)));
    }
}
