using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using R007.Pos.ViewModels;

namespace R007.Pos.Tests;

/// <summary>
/// The WPF app cannot be run on the macOS dev machine, and WPF binding errors only appear at run time. This test is the
/// safety net: it parses every view's XAML and checks that each <c>{Binding ...}</c> path resolves to a real public
/// property on the view model / item type it is bound to, and that two-way bindings target writable properties.
/// </summary>
public sealed partial class XamlBindingTests
{
    private static string ViewsDirectory([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "R007.Pos.App"));

    private static readonly Assembly[] Assemblies = [typeof(ShellViewModel).Assembly, typeof(R007.Pos.Core.Api.Order).Assembly];

    private static readonly XNamespace D = "http://schemas.microsoft.com/expression/blend/2008";

    public static IEnumerable<object[]> XamlFiles() =>
        Directory.GetFiles(ViewsDirectory(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("App.xaml", StringComparison.Ordinal))
            .Select(f => new object[] { Path.GetRelativePath(ViewsDirectory(), f) });

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void EveryBinding_ResolvesOnItsViewModel(string relativePath)
    {
        var doc = XDocument.Load(Path.Combine(ViewsDirectory(), relativePath));
        var root = doc.Root!;
        var designType = root.Attribute(D + "DataContext")?.Value;
        Assert.False(designType is null, $"{relativePath}: add d:DataContext=\"{{d:DesignInstance Type=...}}\" so bindings can be verified");
        var rootType = ResolveDesignType(designType!);
        var problems = new List<string>();

        Walk(root, rootType, rootType, problems, relativePath);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryDataTemplateMapping_PointsAtARealViewType()
    {
        var app = XDocument.Load(Path.Combine(ViewsDirectory(), "App.xaml"));
        var views = typeof(ShellViewModel).Assembly; // view classes live in the WPF assembly, so check the XAML files exist instead
        foreach (var template in app.Descendants().Where(e => e.Name.LocalName == "DataTemplate" && e.Attribute("DataType") is not null))
        {
            var vmName = Regex.Match(template.Attribute("DataType")!.Value, @"\{x:Type \w+:(\w+)\}").Groups[1].Value;
            Assert.NotNull(FindType(vmName));
            var viewName = template.Elements().Single().Name.LocalName;
            Assert.True(File.Exists(Path.Combine(ViewsDirectory(), "Views", viewName + ".xaml")), $"missing view {viewName} for {vmName}");
        }

        Assert.NotNull(views);
    }

    private static void Walk(XElement element, Type? context, Type root, List<string> problems, string file)
    {
        var current = context;
        var localName = element.Name.LocalName;

        if (localName == "DataTemplate" && element.Attribute("DataType") is { } dt)
        {
            var name = Regex.Match(dt.Value, @"\{x:Type \w+:(\w+)\}").Groups[1].Value;
            current = FindType(name);
            if (current is null)
            {
                problems.Add($"{file}: DataTemplate DataType '{dt.Value}' is not a known type");
            }
        }

        foreach (var attribute in element.Attributes())
        {
            var value = attribute.Value;
            if (!value.StartsWith("{Binding", StringComparison.Ordinal))
            {
                continue;
            }

            CheckBinding(element, attribute, value, current, root, problems, file);
        }

        foreach (var child in element.Elements())
        {
            var childContext = current;
            if (child.Name.LocalName is "ItemTemplate" or "DataTemplate" || child.Name.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal))
            {
                // Items of a collection bound through ItemsSource: infer T when the template names no DataType.
                var items = element.Attribute("ItemsSource")?.Value;
                if (items is not null && items.StartsWith("{Binding", StringComparison.Ordinal) && current is not null)
                {
                    var path = ExtractPath(items);
                    var itemType = path is null ? null : ElementTypeOf(Navigate(current, path));
                    if (itemType is not null)
                    {
                        childContext = itemType;
                    }
                }
            }

            Walk(child, childContext, root, problems, file);
        }
    }

    private static void CheckBinding(XElement element, XAttribute attribute, string value, Type? context, Type root, List<string> problems, string file)
    {
        if (value.Contains("ElementName", StringComparison.Ordinal) || Regex.IsMatch(value, @"(?<![A-Za-z])Source\s*="))
        {
            return;
        }

        var path = ExtractPath(value);
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            return;
        }

        var target = context;
        if (value.Contains("RelativeSource", StringComparison.Ordinal))
        {
            if (!path.StartsWith("DataContext.", StringComparison.Ordinal))
            {
                return;
            }

            path = path["DataContext.".Length..];
            target = root; // the AncestorType=UserControl's DataContext is the view's own view model
        }

        if (target is null)
        {
            problems.Add($"{file}: <{element.Name.LocalName} {attribute.Name.LocalName}> binding '{path}' has no known data context");
            return;
        }

        var property = Navigate(target, path, out var owner, out var leaf);
        if (property is null)
        {
            problems.Add($"{file}: <{element.Name.LocalName} {attribute.Name.LocalName}=\"{value}\"> '{path}' not found on {owner?.Name ?? target.Name} (context {target.Name})");
            return;
        }

        var oneWay = Regex.IsMatch(value, @"Mode\s*=\s*(OneWay|OneTime)");
        var twoWayDefault = (element.Name.LocalName, attribute.Name.LocalName) is ("TextBox", "Text") or ("CheckBox", "IsChecked") or ("ListBox", "SelectedItem") or ("ComboBox", "SelectedItem");
        if (twoWayDefault && !oneWay && leaf is not null && leaf.GetSetMethod(nonPublic: false) is null)
        {
            problems.Add($"{file}: <{element.Name.LocalName} {attribute.Name.LocalName}=\"{value}\"> is two-way but '{path}' has no public setter (add Mode=OneWay)");
        }
    }

    private static string? ExtractPath(string binding)
    {
        var inner = binding.TrimStart('{').TrimEnd('}')["Binding".Length..].Trim();
        if (inner.Length == 0)
        {
            return null;
        }

        var parts = SplitTopLevel(inner);
        foreach (var part in parts)
        {
            var p = part.Trim();
            if (p.StartsWith("Path=", StringComparison.Ordinal))
            {
                return p["Path=".Length..].Trim();
            }
        }

        var first = parts[0].Trim();
        return first.Contains('=', StringComparison.Ordinal) ? null : first;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
            }
            else if (text[i] == ',' && depth == 0)
            {
                result.Add(text[start..i]);
                start = i + 1;
            }
        }

        result.Add(text[start..]);
        return result;
    }

    private static Type? Navigate(Type type, string path) => Navigate(type, path, out _, out var leaf) is { } t ? leaf?.PropertyType ?? t : null;

    /// <summary>Walks "A.B.C". Returns the leaf's type (or null when a segment is missing), reporting where it failed.</summary>
    private static Type? Navigate(Type type, string path, out Type? owner, out PropertyInfo? leaf)
    {
        owner = type;
        leaf = null;
        var current = type;
        foreach (var raw in path.Split('.'))
        {
            var name = Regex.Replace(raw, @"\[.*\]$", string.Empty);
            var property = current.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
            {
                owner = current;
                return null;
            }

            leaf = property;
            current = property.PropertyType;
        }

        return current;
    }

    private static Type? ElementTypeOf(Type? collection)
    {
        if (collection is null)
        {
            return null;
        }

        if (collection.IsArray)
        {
            return collection.GetElementType();
        }

        var enumerable = collection.GetInterfaces().Concat([collection]).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? (typeof(IEnumerable).IsAssignableFrom(collection) ? typeof(object) : null);
    }

    private static Type ResolveDesignType(string designInstance)
    {
        var name = Regex.Match(designInstance, @"Type=\w+:(\w+)").Groups[1].Value;
        return FindType(name) ?? throw new InvalidOperationException($"Unknown design-time type '{name}'");
    }

    private static Type? FindType(string simpleName) =>
        Assemblies.SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.Name == simpleName && t.IsPublic);
}
