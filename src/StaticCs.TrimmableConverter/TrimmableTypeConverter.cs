using System.Collections;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using DAM = System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute;

namespace StaticCs;

public static class TrimmableTypeConverter
{
    private static readonly Dictionary<Type, TypeConverter> _intrinsicConverters = new()
    {
        [typeof(bool)] = new BooleanConverter(),
        [typeof(byte)] =  new ByteConverter(),
        [typeof(sbyte)] = new SByteConverter(),
        [typeof(char)] = new CharConverter(),
        [typeof(double)] = new DoubleConverter(),
        [typeof(string)] = new StringConverter(),
        [typeof(int)] = new Int32Converter(),
        [typeof(short)] = new Int16Converter(),
        [typeof(long)] = new Int64Converter(),
        [typeof(float)] = new SingleConverter(),
        [typeof(ushort)] = new UInt16Converter(),
        [typeof(uint)] = new UInt32Converter(),
        [typeof(ulong)] = new UInt64Converter(),
        [typeof(object)] = new TypeConverter(),
        [typeof(CultureInfo)] = new CultureInfoConverter(),
        [typeof(DateTime)] = new DateTimeConverter(),
        [typeof(DateTimeOffset)] = new DateTimeOffsetConverter(),
        [typeof(decimal)] = new DecimalConverter(),
        [typeof(TimeSpan)] = new TimeSpanConverter(),
        [typeof(Guid)] = new GuidConverter(),
        [typeof(Uri)] = new UriTypeConverter(),
    };

    internal const DynamicallyAccessedMemberTypes ConverterAnnotation = DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicFields;

    public static TypeConverter GetConverter([DAM(ConverterAnnotation)] Type type)
    {
        var converter = GetIntrinsicConverter(type);
        if (converter != null)
        {
            return converter;
        }

        var attribute = type.GetCustomAttribute<TypeConverterAttribute>();
        if (attribute != null)
        {
            var converterType = Type.GetType(attribute.ConverterTypeName, false, false);
            if (converterType != null)
            {
                return (TypeConverter)Activator.CreateInstance(converterType)!;
            }
        }

        return new TypeConverter();
    }

    public static TypeConverter GetConverter<[DAM(ConverterAnnotation)] T>() => GetConverter(typeof(T));

    private static readonly ArrayConverter s_arrayConverter = new();
    private static readonly CollectionConverter s_collectionConverter = new();

    /// <summary>
    /// A highly-constrained version of <see cref="TypeDescriptor.GetConverter(Type)" /> that only returns intrinsic converters.
    /// </summary>
    private static TypeConverter? GetIntrinsicConverter([DAM(ConverterAnnotation)] Type type)
    {
        if (type.IsEnum)
        {
            return new EnumConverter(type);
        }

        if (type.IsArray)
        {
            return s_arrayConverter;
        }

        if (typeof(ICollection).IsAssignableFrom(type))
        {
            return s_collectionConverter;
        }

        if (_intrinsicConverters.TryGetValue(type, out var converter))
        {
            return converter;
        }

        return null;
    }
}

