using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Godot.SourceGenerators
{
    [Generator]
    public class ScriptPropertiesGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.AreGodotSourceGeneratorsDisabled())
                return;

            INamedTypeSymbol[] godotClasses = context
                .Compilation.SyntaxTrees
                .SelectMany(tree =>
                    tree.GetRoot().DescendantNodes()
                        .OfType<ClassDeclarationSyntax>()
                        .SelectGodotScriptClasses(context.Compilation)
                        // Report and skip non-partial classes
                        .Where(x =>
                        {
                            if (x.cds.IsPartial())
                            {
                                if (x.cds.IsNested() && !x.cds.AreAllOuterTypesPartial(out var typeMissingPartial))
                                {
                                    Common.ReportNonPartialGodotScriptOuterClass(context, typeMissingPartial!);
                                    return false;
                                }

                                return true;
                            }

                            Common.ReportNonPartialGodotScriptClass(context, x.cds, x.symbol);
                            return false;
                        })
                        .Select(x => x.symbol)
                )
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                .ToArray();

            if (godotClasses.Length > 0)
            {
                var typeCache = new MarshalUtils.TypeCache(context);

                foreach (var godotClass in godotClasses)
                {
                    VisitGodotScriptClass(context, typeCache, godotClass);
                }
            }
        }

        private static void VisitGodotScriptClass(
            GeneratorExecutionContext context,
            MarshalUtils.TypeCache typeCache,
            INamedTypeSymbol symbol
        )
        {
            INamespaceSymbol namespaceSymbol = symbol.ContainingNamespace;
            string classNs = namespaceSymbol != null && !namespaceSymbol.IsGlobalNamespace ?
                namespaceSymbol.FullQualifiedName() :
                string.Empty;
            bool hasNamespace = classNs.Length != 0;

            bool isInnerClass = symbol.ContainingType != null;

            string uniqueHint = symbol.FullQualifiedName().SanitizeQualifiedNameForUniqueHint()
                                + "_ScriptProperties_Generated";

            var source = new StringBuilder();

            source.Append("using Godot;\n");
            source.Append("using Godot.NativeInterop;\n");
            source.Append("\n");

            if (hasNamespace)
            {
                source.Append("namespace ");
                source.Append(classNs);
                source.Append(" {\n\n");
            }

            if (isInnerClass)
            {
                var containingType = symbol.ContainingType;

                while (containingType != null)
                {
                    source.Append("partial ");
                    source.Append(containingType.GetDeclarationKeyword());
                    source.Append(" ");
                    source.Append(containingType.NameWithTypeParameters());
                    source.Append("\n{\n");

                    containingType = containingType.ContainingType;
                }
            }

            source.Append("partial class ");
            source.Append(symbol.NameWithTypeParameters());
            source.Append("\n{\n");

            var members = symbol.GetMembers();

            var propertySymbols = members
                .Where(s => !s.IsStatic && s.Kind == SymbolKind.Property)
                .Cast<IPropertySymbol>();

            var fieldSymbols = members
                .Where(s => !s.IsStatic && s.Kind == SymbolKind.Field && !s.IsImplicitlyDeclared)
                .Cast<IFieldSymbol>();

            var godotClassProperties = propertySymbols.WhereIsGodotCompatibleType(typeCache).ToArray();
            var godotClassFields = fieldSymbols.WhereIsGodotCompatibleType(typeCache).ToArray();

            source.Append("    private partial class GodotInternal {\n");

            // Generate cached StringNames for methods and properties, for fast lookup

            foreach (var property in godotClassProperties)
            {
                string propertyName = property.PropertySymbol.Name;
                source.Append("        public static readonly StringName PropName_");
                source.Append(propertyName);
                source.Append(" = \"");
                source.Append(propertyName);
                source.Append("\";\n");
            }

            foreach (var field in godotClassFields)
            {
                string fieldName = field.FieldSymbol.Name;
                source.Append("        public static readonly StringName PropName_");
                source.Append(fieldName);
                source.Append(" = \"");
                source.Append(fieldName);
                source.Append("\";\n");
            }

            source.Append("    }\n"); // class GodotInternal

            // Generate GetGodotPropertyList

            if (godotClassProperties.Length > 0 || godotClassFields.Length > 0)
            {
                string dictionaryType = "System.Collections.Generic.List<global::Godot.Bridge.PropertyInfo>";

                source.Append("    internal static ")
                    .Append(dictionaryType)
                    .Append(" GetGodotPropertyList()\n    {\n");

                source.Append("        var properties = new ")
                    .Append(dictionaryType)
                    .Append("();\n");

                foreach (var property in godotClassProperties)
                {
                    var propertyInfo = DeterminePropertyInfo(context, typeCache,
                        property.PropertySymbol, property.Type);

                    if (propertyInfo == null)
                        continue;

                    AppendPropertyInfo(source, propertyInfo.Value);
                }

                foreach (var field in godotClassFields)
                {
                    var propertyInfo = DeterminePropertyInfo(context, typeCache,
                        field.FieldSymbol, field.Type);

                    if (propertyInfo == null)
                        continue;

                    AppendPropertyInfo(source, propertyInfo.Value);
                }

                source.Append("        return properties;\n");
                source.Append("    }\n");
            }

            source.Append("}\n"); // partial class

            if (isInnerClass)
            {
                var containingType = symbol.ContainingType;

                while (containingType != null)
                {
                    source.Append("}\n"); // outer class

                    containingType = containingType.ContainingType;
                }
            }

            if (hasNamespace)
            {
                source.Append("\n}\n");
            }

            context.AddSource(uniqueHint, SourceText.From(source.ToString(), Encoding.UTF8));
        }

        private static void AppendPropertyInfo(StringBuilder source, PropertyInfo propertyInfo)
        {
            source.Append("        properties.Add(new(type: (Godot.Variant.Type)")
                .Append((int)propertyInfo.Type)
                .Append(", name: GodotInternal.PropName_")
                .Append(propertyInfo.Name)
                .Append(", hint: (Godot.PropertyHint)")
                .Append((int)propertyInfo.Hint)
                .Append(", hintString: \"")
                .Append(propertyInfo.HintString)
                .Append("\", usage: (Godot.PropertyUsageFlags)")
                .Append((int)propertyInfo.Usage)
                .Append(", exported: ")
                .Append(propertyInfo.Exported ? "true" : "false")
                .Append("));\n");
        }

        private static PropertyInfo? DeterminePropertyInfo(
            GeneratorExecutionContext context,
            MarshalUtils.TypeCache typeCache,
            ISymbol memberSymbol,
            MarshalType marshalType
        )
        {
            var exportAttr = memberSymbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.IsGodotExportAttribute() ?? false);

            var propertySymbol = memberSymbol as IPropertySymbol;
            var fieldSymbol = memberSymbol as IFieldSymbol;

            if (exportAttr != null && propertySymbol != null)
            {
                if (propertySymbol.GetMethod == null)
                {
                    Common.ReportExportedMemberIsWriteOnly(context, propertySymbol);
                    return null;
                }

                if (propertySymbol.SetMethod == null)
                {
                    Common.ReportExportedMemberIsReadOnly(context, propertySymbol);
                    return null;
                }
            }

            var memberType = propertySymbol?.Type ?? fieldSymbol!.Type;

            var memberVariantType = MarshalUtils.ConvertMarshalTypeToVariantType(marshalType)!.Value;
            string memberName = memberSymbol.Name;

            if (exportAttr == null)
            {
                return new PropertyInfo(memberVariantType, memberName, PropertyHint.None,
                    hintString: null, PropertyUsageFlags.ScriptVariable, exported: false);
            }

            if (!TryGetMemberExportHint(typeCache, memberType, exportAttr, memberVariantType,
                    isTypeArgument: false, out var hint, out var hintString))
            {
                var constructorArguments = exportAttr.ConstructorArguments;

                if (constructorArguments.Length > 0)
                {
                    var hintValue = exportAttr.ConstructorArguments[0].Value;
                    hint = hintValue != null ? (PropertyHint)(int)hintValue : PropertyHint.None;

                    hintString = constructorArguments.Length > 1 ?
                        exportAttr.ConstructorArguments[1].Value?.ToString() :
                        null;
                }
                else
                {
                    hint = PropertyHint.None;
                }
            }

            var propUsage = PropertyUsageFlags.Default | PropertyUsageFlags.ScriptVariable;

            if (memberVariantType == VariantType.Nil)
                propUsage |= PropertyUsageFlags.NilIsVariant;

            return new PropertyInfo(memberVariantType, memberName,
                hint, hintString, propUsage, exported: true);
        }

        private static bool TryGetMemberExportHint(
            MarshalUtils.TypeCache typeCache,
            ITypeSymbol type, AttributeData exportAttr,
            VariantType variantType, bool isTypeArgument,
            out PropertyHint hint, out string? hintString
        )
        {
            hint = PropertyHint.None;
            hintString = null;

            if (variantType == VariantType.Nil)
                return true; // Variant, no export hint

            if (variantType == VariantType.Int &&
                type.IsValueType && type.TypeKind == TypeKind.Enum)
            {
                hint = PropertyHint.Enum;

                var members = type.GetMembers();

                var enumFields = members
                    .Where(s => s.Kind == SymbolKind.Field && s.IsStatic &&
                                s.DeclaredAccessibility == Accessibility.Public &&
                                !s.IsImplicitlyDeclared)
                    .Cast<IFieldSymbol>().ToArray();

                var hintStringBuilder = new StringBuilder();
                var nameOnlyHintStringBuilder = new StringBuilder();

                // True: enum Foo { Bar, Baz, Qux }
                // True: enum Foo { Bar = 0, Baz = 1, Qux = 2 }
                // False: enum Foo { Bar = 0, Baz = 7, Qux = 5 }
                bool usesDefaultValues = true;

                for (int i = 0; i < enumFields.Length; i++)
                {
                    var enumField = enumFields[i];

                    if (i > 0)
                    {
                        hintStringBuilder.Append(",");
                        nameOnlyHintStringBuilder.Append(",");
                    }

                    string enumFieldName = enumField.Name;
                    hintStringBuilder.Append(enumFieldName);
                    nameOnlyHintStringBuilder.Append(enumFieldName);

                    long val = enumField.ConstantValue switch
                    {
                        sbyte v => v,
                        short v => v,
                        int v => v,
                        long v => v,
                        byte v => v,
                        ushort v => v,
                        uint v => v,
                        ulong v => (long)v,
                        _ => 0
                    };

                    if (val != (uint)i)
                        usesDefaultValues = false;

                    hintStringBuilder.Append(":");
                    nameOnlyHintStringBuilder.Append(val);
                }

                hintString = !usesDefaultValues ?
                    hintStringBuilder.ToString() :
                    // If we use the format NAME:VAL, that's what the editor displays.
                    // That's annoying if the user is not using custom values for the enum constants.
                    // This may not be needed in the future if the editor is changed to not display values.
                    nameOnlyHintStringBuilder.ToString();

                return true;
            }

            if (variantType == VariantType.Object && type is INamedTypeSymbol memberNamedType &&
                memberNamedType.InheritsFrom("GodotSharp", "Godot.Resource"))
            {
                string nativeTypeName = memberNamedType.GetGodotScriptNativeClassName()!;

                hint = PropertyHint.ResourceType;
                hintString = nativeTypeName;

                return true;
            }

            static bool GetStringArrayEnumHint(VariantType elementVariantType,
                AttributeData exportAttr, out string? hintString)
            {
                var constructorArguments = exportAttr.ConstructorArguments;

                if (constructorArguments.Length > 0)
                {
                    var presetHintValue = exportAttr.ConstructorArguments[0].Value;
                    var presetHint = presetHintValue != null ?
                        (PropertyHint)(int)presetHintValue :
                        PropertyHint.None;

                    if (presetHint == PropertyHint.Enum)
                    {
                        string? presetHintString = constructorArguments.Length > 1 ?
                            exportAttr.ConstructorArguments[1].Value?.ToString() :
                            null;

                        hintString = (int)elementVariantType + "/" + (int)PropertyHint.Enum + ":";

                        if (presetHintString != null)
                            hintString += presetHintString;

                        return true;
                    }
                }

                hintString = null;
                return false;
            }

            if (!isTypeArgument && variantType == VariantType.Array)
            {
                var elementType = MarshalUtils.GetArrayElementType(type);

                if (elementType == null)
                    return false; // Non-generic Array, so there's no hint to add

                var elementMarshalType = MarshalUtils.ConvertManagedTypeToMarshalType(elementType, typeCache)!.Value;
                var elementVariantType = MarshalUtils.ConvertMarshalTypeToVariantType(elementMarshalType)!.Value;

                bool isPresetHint = false;

                if (elementVariantType == VariantType.String)
                    isPresetHint = GetStringArrayEnumHint(elementVariantType, exportAttr, out hintString);

                if (!isPresetHint)
                {
                    bool hintRes = TryGetMemberExportHint(typeCache, elementType,
                        exportAttr, elementVariantType, isTypeArgument: true,
                        out var elementHint, out var elementHintString);

                    if (hintRes)
                    {
                        // Format: type/hint:hint_string
                        hintString = (int)elementVariantType + "/" + (int)elementHint + ":";

                        if (elementHintString != null)
                            hintString += elementHintString;
                    }
                }

                hint = PropertyHint.TypeString;

                return hintString != null;
            }

            if (!isTypeArgument && variantType == VariantType.PackedStringArray)
            {
                if (GetStringArrayEnumHint(VariantType.String, exportAttr, out hintString))
                {
                    hint = PropertyHint.TypeString;
                    return true;
                }
            }

            if (!isTypeArgument && variantType == VariantType.Dictionary)
            {
                // TODO: Dictionaries are not supported in the inspector
                return false;
            }

            return false;
        }
    }
}