namespace Seiton.Core.Parsing;

/// <summary>
/// Base class for the expression type hierarchy (Spec §7.3).
/// Instances of concrete subtypes are available as static singletons on this class.
/// </summary>
public abstract class ExprType
{
    // Prevent external subclassing.
    private protected ExprType() { }

    /// <summary>Any / unknown type. Compatible with all other types when assigning.</summary>
    public static ExprType Any { get; } = new AnyExprType();

    /// <summary>The <c>null</c> literal type.</summary>
    public static ExprType Null { get; } = new NullExprType();

    /// <summary>Boolean type (<c>true</c> / <c>false</c>).</summary>
    public static ExprType Bool { get; } = new BoolExprType();

    /// <summary>Numeric type (integer or float literals).</summary>
    public static ExprType Number { get; } = new NumberExprType();

    /// <summary>String type.</summary>
    public static ExprType String { get; } = new StringExprType();

    /// <summary>Object type without known properties.</summary>
    public static ObjectExprType EmptyObject { get; } = new ObjectExprType();

    /// <summary>Array type whose element type is Any.</summary>
    public static ArrayExprType EmptyArray { get; } = new ArrayExprType(Any);

    /// <summary>Human-readable name for use in diagnostic messages.</summary>
    public abstract string TypeName { get; }

    /// <summary>
    /// Returns true when a value of this type can be used where <paramref name="target"/> is expected.
    /// <c>Any</c> is universally assignable to and from every type.
    /// </summary>
    public virtual bool IsAssignableTo(ExprType target)
    {
        if (target is AnyExprType || this is AnyExprType)
        {
            return true;
        }

        return GetType() == target.GetType();
    }
}

/// <summary>Any / unknown type.</summary>
public sealed class AnyExprType : ExprType
{
    internal AnyExprType() { }

    public override string TypeName => "any";
}

/// <summary>Null literal type.</summary>
public sealed class NullExprType : ExprType
{
    internal NullExprType() { }

    public override string TypeName => "null";
}

/// <summary>Boolean type.</summary>
public sealed class BoolExprType : ExprType
{
    internal BoolExprType() { }

    public override string TypeName => "bool";
}

/// <summary>Numeric type.</summary>
public sealed class NumberExprType : ExprType
{
    internal NumberExprType() { }

    public override string TypeName => "number";
}

/// <summary>String type.</summary>
public sealed class StringExprType : ExprType
{
    internal StringExprType() { }

    public override string TypeName => "string";
}

/// <summary>Object type, optionally with a static property map.</summary>
public sealed class ObjectExprType : ExprType
{
    internal ObjectExprType() { }

    public override string TypeName => "object";
}

/// <summary>Array type with a known element type.</summary>
public sealed class ArrayExprType : ExprType
{
    internal ArrayExprType(ExprType elementType)
    {
        ElementType = elementType;
    }

    /// <summary>The type of each element in the array.</summary>
    public ExprType ElementType { get; }

    public override string TypeName => "array";
}
