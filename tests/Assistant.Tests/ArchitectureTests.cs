// Règle de dépendance (séquence 2.2), vérifiée sur les assemblies compilés : ce que le compilateur a
// produit, quelle que soit la façon dont les sources l'écrivent.

using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Assistant.Application;
using Assistant.Cli;
using Assistant.Domain;
using Assistant.Infrastructure;
using Xunit;

// Échantillons du test du test (voir LeakSamples) : un attribut d'assembly et un de module qui nomment l'infrastructure.
[assembly: Assistant.Tests.LeakSamples.Marker(typeof(JsonVectorIndex))]
[module: Assistant.Tests.LeakSamples.Marker(typeof(InMemoryVectorIndex))]

namespace Assistant.Tests;

/// <summary>Règle de dépendance, vérifiée sur les assemblies compilés (séquence 2.2).</summary>
public class ArchitectureTests
{
    private static IEnumerable<string> References(Type anyTypeOfAssembly) =>
        anyTypeOfAssembly.Assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static readonly HashSet<string> DomainAllowed = new()
    {
        "System.Runtime", "System.Collections", "System.Linq", "System.Text.RegularExpressions", "System.Memory", "netstandard",
        "System.Text.Json",   // bibliothèque standard, permise au cœur
    };

    [Fact]
    public void Domain_depends_on_nothing_but_the_runtime() =>
        // Liste blanche : System.Net.Http dans le domaine ferait échouer ce test.
        Assert.All(References(typeof(Document)), name => Assert.Contains(name, DomainAllowed));

    [Fact]
    public void Application_depends_only_on_the_domain() =>
        Assert.All(References(typeof(AskQuestion)).Where(n => !n.StartsWith("System", StringComparison.Ordinal)),
                   name => Assert.Equal("Assistant.Domain", name));

    [Fact]
    public void The_core_does_not_touch_the_network()
    {
        // Le réseau est un détail de l'infrastructure. JSON, lui, est permis au cœur : c'est la bibliothèque standard. Si
        // l'identité de l'index passe par Fingerprints.PythonJson, c'est pour écrire à la main ce que System.Text.Json
        // n'écrit pas comme json.dumps(sort_keys=True) : les séparateurs « , » et « : » suivis d'une espace, et les clés
        // triées.
        foreach (var core in new[] { typeof(AskQuestion), typeof(Document) })
        {
            Assert.DoesNotContain(References(core), n => n.StartsWith("System.Net", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Adapters_are_assembled_only_by_the_composition_root()
    {
        // Aucun constructeur public de l'infrastructure ne prend un autre type concret de l'infrastructure :
        // les décorateurs ne reçoivent que des ports, et personne d'autre que Composition n'empile.
        var infrastructure = typeof(HttpEmbedder).Assembly;
        var concrete = infrastructure.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && !typeof(Delegate).IsAssignableFrom(t)).ToHashSet();
        foreach (var type in concrete)
        {
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    Assert.False(concrete.Contains(parameter.ParameterType),
                                 $"{type.Name}({parameter.Name}) reçoit un adaptateur concret au lieu d'un port");
                }
            }
        }
    }

    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static;

    // Table des opcodes, tirée de System.Reflection.Emit.OpCodes : valeur (un octet, ou 0xFE suivi d'un octet) → opcode.
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => (ushort)opcode.Value);

    /// <summary>
    /// Ce que les types de <paramref name="assembly"/> prennent à l'assembly <paramref name="infrastructure"/>, hors de
    /// <paramref name="compositionRoot"/> et des types qu'il imbrique (le compilateur y range les fermetures et les
    /// itérateurs qu'il écrit pour lui). Lu dans le code compilé : type de base et interfaces, signatures, contraintes
    /// génériques, attributs (de l'assembly, du module, des types, des membres, des paramètres, du retour et des
    /// paramètres génériques, avec leurs arguments typeof), variables locales, clauses catch, et ce que désigne chaque
    /// jeton de l'IL (appel, champ, type, typeof) avec la signature du membre désigné. Un type de la racine compte pour
    /// ce dont il hérite : sa sous-classe d'adaptateur reste un adaptateur pour qui s'en sert. L'écriture des sources n'y
    /// change rien : alias, global using, &lt;Using&gt; du projet, identifiants @, commentaires, échappements Unicode,
    /// autre type nommé Composition (la racine est un type, ses déclarations partielles comprises, pas un fichier).
    /// Limites : le corps d'une méthode de la racine n'est pas suivi (une fabrique qui renvoie un adaptateur typé object
    /// passe) ; une constante (const) d'un adaptateur est recopiée à la compilation sans laisser de
    /// trace ; un chargement par réflexion (Type.GetType("…")) ne nomme rien ; un argument d'attribut de type énuméré
    /// (l'infrastructure n'a pas d'énumération) et un pointeur de fonction (code unsafe, interdit dans Assistant.Cli)
    /// ne sont pas lus. Le test attrape les erreurs, pas la malveillance.
    /// </summary>
    internal static List<string> InfrastructureLeaks(Assembly assembly, Type compositionRoot, Assembly infrastructure,
                                                     Func<Type, bool>? inScope = null)
    {
        var leaks = new SortedSet<string>(StringComparer.Ordinal);

        // Le type de l'infrastructure que nomme `type` : lui-même, son élément (tableau, ref), un argument générique, et
        // pour un type de l'assembly analysé, son type de base et ses interfaces (pour un paramètre générique : ses
        // contraintes). Les autres n'héritent pas de l'infrastructure, que ni le runtime ni l'application ne connaissent :
        // les parcourir aussi ne changerait rien, et coûterait une minute. `visiting` coupe les cycles (Container :
        // IEquatable<Container>).
        var visiting = new HashSet<Type>();
        Type? Find(Type? type)
        {
            if (type is null || !visiting.Add(type))
            {
                return null;
            }
            try
            {
                if (type.HasElementType)
                {
                    return Find(type.GetElementType());
                }
                if (type.Assembly == infrastructure)
                {
                    return type;
                }
                return First(type.Assembly == assembly
                    ? [.. type.GetGenericArguments(), type.BaseType, .. type.GetInterfaces()]
                    : [.. type.GetGenericArguments()]);
            }
            finally
            {
                visiting.Remove(type);
            }
        }
        Type? First(IEnumerable<Type?> types) => types.Select(Find).FirstOrDefault(found => found is not null);

        void Check(string owner, IEnumerable<Type?> types)
        {
            foreach (var found in types.Select(Find).OfType<Type>())
            {
                leaks.Add($"{owner} utilise {found.FullName}");
            }
        }

        Check($"[assembly: {assembly.GetName().Name}]", Attributes(assembly.GetCustomAttributesData()));
        Check($"[module: {assembly.ManifestModule.Name}]", Attributes(assembly.ManifestModule.GetCustomAttributesData()));
        foreach (var type in assembly.GetTypes().Where(t => (inScope?.Invoke(t) ?? true) && !IsWithin(t, compositionRoot)))
        {
            var members = type.GetMembers(Declared).Where(member => member is not Type).ToList();
            var methods = members.OfType<MethodBase>().ToList();
            var genericParameters = type.GetGenericArguments()
                .Concat(methods.Where(m => m.IsGenericMethodDefinition).SelectMany(m => m.GetGenericArguments()))
                .ToList();
            var parameters = methods.SelectMany(m => m.GetParameters())
                .Concat(methods.OfType<MethodInfo>().Select(m => m.ReturnParameter));
            Check(SourceName(type), members.SelectMany(Signature).Prepend(type)
                .Concat(Attributes(members.Prepend(type).Concat(genericParameters).SelectMany(m => m.GetCustomAttributesData())))
                .Concat(Attributes(parameters.SelectMany(p => p.GetCustomAttributesData())))
                .Concat(methods.SelectMany(Body)));
        }
        return leaks.ToList();
    }

    // Les types que nomme un membre : son type déclarant et sa signature. Appliqué aux jetons de l'IL, il fait compter
    // chez celui qui s'en sert une fabrique, un champ ou une sous-classe de la racine qui nomme un adaptateur. Une
    // propriété ou un événement ne nomme rien de plus que ses accesseurs, lus comme méthodes.
    private static IEnumerable<Type?> Signature(MemberInfo member) => member switch
    {
        Type type => [type],
        FieldInfo field => [field.DeclaringType, field.FieldType],
        MethodInfo method => [method.DeclaringType, method.ReturnType, .. method.GetParameters().Select(p => p.ParameterType),
                              .. (method.IsGenericMethod ? method.GetGenericArguments() : [])],
        ConstructorInfo constructor => [constructor.DeclaringType, .. constructor.GetParameters().Select(p => p.ParameterType)],
        _ => [],
    };

    // Les types que nomment des attributs : le leur (générique compris), et leurs arguments typeof, en tableau ou nommés.
    private static IEnumerable<Type?> Attributes(IEnumerable<CustomAttributeData> attributes) =>
        attributes.SelectMany(attribute => attribute.ConstructorArguments
            .Concat(attribute.NamedArguments.Select(named => named.TypedValue))
            .SelectMany(Argument)
            .Prepend(attribute.AttributeType));

    private static IEnumerable<Type?> Argument(CustomAttributeTypedArgument argument) =>
        argument.Value is IEnumerable<CustomAttributeTypedArgument> items ? items.SelectMany(Argument) : [argument.Value as Type];

    // Le corps d'une méthode : variables locales, clauses catch, et ce que désigne chaque jeton de l'IL.
    private static IEnumerable<Type?> Body(MethodBase method) => method.GetMethodBody() is { } body
        ? body.LocalVariables.Select(local => local.LocalType)
            .Concat(body.ExceptionHandlingClauses.Where(c => c.Flags == ExceptionHandlingClauseOptions.Clause).Select(c => c.CatchType))
            .Concat(Tokens(method, body.GetILAsByteArray()!).SelectMany(Signature))
        : [];

    /// <summary>Les membres que désignent les jetons de l'IL d'une méthode, lue opcode par opcode.</summary>
    private static List<MemberInfo> Tokens(MethodBase method, byte[] il)
    {
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var members = new List<MemberInfo>();
        for (var at = 0; at < il.Length;)
        {
            var opcode = OpCodesByValue[il[at] == 0xFE ? (ushort)(0xFE00 | il[at + 1]) : il[at]];
            at += opcode.Size;
            if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                members.Add(method.Module.ResolveMember(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(at)), typeArguments, methodArguments)!);
            }
            at += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(at)),
                _ => 4,   // jeton, signature (calli), chaîne, branche longue, entier ou réel sur 4 octets
            };
        }
        return members;
    }

    private static bool IsWithin(Type type, Type outer)
    {
        for (Type? current = type; current is not null; current = current.DeclaringType)
        {
            if (current == outer)
            {
                return true;
            }
        }
        return false;
    }

    // Le type tel que l'écrivent les sources : une fermeture ou un itérateur est rapporté au type qui le contient.
    private static string SourceName(Type type)
    {
        while (type.DeclaringType is { } outer && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
        {
            type = outer;
        }
        return type.FullName!.Replace('+', '.');
    }

    [Fact]
    public void Only_the_composition_root_knows_the_infrastructure()
    {
        // Dans la couche interface (Assistant.Cli), seul le type Composition touche à l'infrastructure, et les autres ne
        // lui reprennent rien dont la déclaration la nomme (champ, signature, sous-classe) ; le corps de ses méthodes
        // n'est pas suivi. Vérifié sur l'assembly compilé, pas sur le texte des sources.
        var leaks = InfrastructureLeaks(typeof(Composition).Assembly, typeof(Composition), typeof(HttpEmbedder).Assembly);
        Assert.True(leaks.Count == 0, $"seule Composition peut toucher à l'infrastructure : {string.Join(", ", leaks)}");
    }

    [Fact]
    public void The_rule_reads_the_compiled_code_whatever_the_sources_say()
    {
        // Test du test, sur LeakSamples : chaque échantillon fait entrer l'infrastructure par une seule voie du code
        // compilé, et chacune doit être vue. LeakSamples.Composition joue la racine : ni elle ni ce que le compilateur
        // génère pour elle n'est signalé ; un homonyme rangé ailleurs, si.
        var leaks = InfrastructureLeaks(typeof(LeakSamples).Assembly, typeof(LeakSamples.Composition), typeof(HttpEmbedder).Assembly,
                                        inScope: t => IsWithin(t, typeof(LeakSamples)));
        const string Sample = "Assistant.Tests.LeakSamples.";
        const string Json = " utilise Assistant.Infrastructure.JsonVectorIndex";
        const string InMemory = " utilise Assistant.Infrastructure.InMemoryVectorIndex";
        var expected = new[]
        {
            Sample + "Constructs" + Json, Sample + "StaticInitializer" + Json, Sample + "Deferred" + Json,
            Sample + "Elsewhere.Composition" + Json,
            Sample + "TypeTest" + Json, Sample + "TypeOf" + InMemory, Sample + "LocalVariable" + Json,
            Sample + "CatchClause utilise Assistant.Infrastructure.IndexUnreadableException",
            Sample + "ParameterType" + Json, Sample + "ArrayField" + Json, Sample + "GenericArgument" + Json,
            Sample + "GenericCall" + Json, Sample + "StaticCall" + InMemory, Sample + "InGenericType`1" + Json,
            Sample + "JumpTable" + Json, Sample + "IEnumeratesIndexes" + Json,
            Sample + "Constrained`1" + InMemory, Sample + "ConstrainedMethod" + InMemory,
            Sample + "Factory" + Json, Sample + "RootField" + Json, Sample + "NestedSubclass" + InMemory,
            Sample + "NestedField" + InMemory, Sample + "NestedConstructor" + Json,
            Sample + "Attributed" + Json, Sample + "ParameterAttribute" + Json, Sample + "AttributedMember" + Json,
            Sample + "AttributedReturn" + Json, Sample + "AttributedTypeParameter`1" + Json,
            Sample + "AttributedMethodTypeParameter" + Json,
            Sample + "AttributedNamedArray" + Json, Sample + "AttributedGenerically" + Json,
            "[assembly: Assistant.Tests]" + Json, "[module: Assistant.Tests.dll]" + InMemory,
        };
        Assert.Equal(expected.Order(StringComparer.Ordinal), leaks);
    }
}

/// <summary>
/// Échantillons du test du test : chacun fait entrer l'infrastructure par une seule voie du code compilé.
/// <see cref="Composition"/> y joue la racine de composition.
/// </summary>
public static class LeakSamples
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class MarkerAttribute(Type type) : Attribute
    {
        public Type Type { get; } = type;

        public Type[]? Also { get; set; }
    }

    [AttributeUsage(AttributeTargets.All)]
    public sealed class MarkerAttribute<T> : Attribute
    {
    }

    // La racine : tout lui est permis, à elle comme à la fermeture et à l'itérateur que le compilateur écrit pour elle.
    public static class Composition
    {
        public static readonly JsonVectorIndex Shared = new("x");

        public static JsonVectorIndex Open(string path) => new(path);

        public static Func<object> Later => () => new JsonVectorIndex("x");

        public static IEnumerable<object> All()
        {
            yield return new JsonVectorIndex("x");
        }

        // Une sous-classe d'adaptateur rangée dans la racine : pour qui s'en sert ailleurs, c'est l'adaptateur.
        public sealed class Memo : InMemoryVectorIndex
        {
            public static int Count;
        }

        public sealed class Holder
        {
            public Holder(JsonVectorIndex index)
            {
            }
        }
    }

    // Même nom, autre type : pas d'exemption (l'ancien test exemptait tout fichier nommé Composition.cs).
    public static class Elsewhere
    {
        public static class Composition
        {
            public static object Open() => new JsonVectorIndex("x");
        }
    }

    // Écriture tordue (identifiant verbatim, commentaire, échappement Unicode) : l'IL est celui de new JsonVectorIndex("x").
    public static class Constructs
    {
        public static object Open() => new global::@Assistant./**/\u0049nfrastructure.JsonVectorIndex("x");
    }

    public static class StaticInitializer
    {
        public static readonly object Index = new JsonVectorIndex("x");
    }

    public static class Deferred
    {
        public static Func<object> Later => () => new JsonVectorIndex("x");
    }

    public static class TypeTest
    {
        public static bool IsJson(IVectorIndex index) => index is JsonVectorIndex;
    }

    public static class TypeOf
    {
        public static Type Get() => typeof(InMemoryVectorIndex);
    }

    // Une variable de type adaptateur, sans appel ni conversion : seule la table des variables locales la nomme.
    public static class LocalVariable
    {
        public static bool Empty()
        {
            JsonVectorIndex? index = null;
            ref var slot = ref index;   // une adresse prise : la variable reste, même compilée en Release
            return slot is null;
        }
    }

    public static class CatchClause
    {
        public static string? Read(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IndexUnreadableException)
            {
                return null;
            }
        }
    }

    public static class ParameterType
    {
        public static void Use(JsonVectorIndex index)
        {
        }
    }

    public static class ArrayField
    {
        public static JsonVectorIndex[]? Indexes;
    }

    public static class GenericArgument
    {
        public static List<JsonVectorIndex>? Indexes;
    }

    // Un appel générique dont la signature ne nomme pas l'adaptateur : seul l'argument de type le nomme.
    public static class GenericCall
    {
        public static bool Check() => RuntimeHelpers.IsReferenceOrContainsReferences<JsonVectorIndex>();
    }

    // Un appel dont la signature ne nomme pas l'adaptateur : seul le type qui déclare la méthode le nomme.
    public static class StaticCall
    {
        public static double[] Unit() => InMemoryVectorIndex.Normalize(new[] { 1.0 });
    }

    // Dans un type générique, un jeton qui nomme T ne se lit qu'avec les arguments génériques du type.
    public sealed class InGenericType<T>
        where T : notnull
    {
        public static object Open() => new Dictionary<T, JsonVectorIndex>();
    }

    // Un switch compilé en table de sauts : il faut sauter toute la table pour lire les jetons qui la suivent.
    public static class JumpTable
    {
        public static object Pick(int n) => n switch
        {
            0 => "a", 1 => "b", 2 => "c", 3 => "d", 4 => "e", 5 => "f", 6 => "g", 7 => "h",
            _ => new JsonVectorIndex("x"),
        };
    }

    public interface IEnumeratesIndexes : IEnumerable<JsonVectorIndex>
    {
    }

    public sealed class Constrained<T> where T : InMemoryVectorIndex
    {
    }

    public static class ConstrainedMethod
    {
        public static void Use<T>() where T : InMemoryVectorIndex
        {
        }
    }

    // L'adaptateur repris à la racine : par une de ses fabriques, un de ses champs, sa sous-classe d'adaptateur
    // (construite ou lue par un champ) ou un constructeur qui en prend un.
    public static class Factory
    {
        public static object Open() => Composition.Open("x");
    }

    public static class RootField
    {
        public static object Read() => Composition.Shared;
    }

    public static class NestedSubclass
    {
        public static object Open() => new Composition.Memo();
    }

    public static class NestedField
    {
        public static int Read() => Composition.Memo.Count;
    }

    public static class NestedConstructor
    {
        public static object Open() => new Composition.Holder(null!);
    }

    [Marker(typeof(JsonVectorIndex))]
    public static class Attributed
    {
    }

    public static class ParameterAttribute
    {
        public static void Use([Marker(typeof(JsonVectorIndex))] int value)
        {
        }
    }

    public static class AttributedMember
    {
        [Marker(typeof(JsonVectorIndex))]
        public static void Use()
        {
        }
    }

    public static class AttributedReturn
    {
        [return: Marker(typeof(JsonVectorIndex))]
        public static int Get() => 0;
    }

    public sealed class AttributedTypeParameter<[Marker(typeof(JsonVectorIndex))] T>
    {
    }

    public static class AttributedMethodTypeParameter
    {
        public static void Use<[Marker(typeof(JsonVectorIndex))] T>()
        {
        }
    }

    [Marker(typeof(int), Also = new[] { typeof(JsonVectorIndex) })]
    public static class AttributedNamedArray
    {
    }

    [Marker<JsonVectorIndex>]
    public static class AttributedGenerically
    {
    }
}
