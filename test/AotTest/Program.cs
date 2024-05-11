// See https://aka.ms/new-console-template for more information

using StaticCs;
using System.ComponentModel;
using Xunit;

// Primitive types
Assert.IsType<ByteConverter>(TrimmableTypeConverter.GetConverter(typeof(byte)));
Assert.IsType<BooleanConverter>(TrimmableTypeConverter.GetConverter(typeof(bool)));
Assert.IsType<Int32Converter>(TrimmableTypeConverter.GetConverter(typeof(int)));
Assert.IsType<UInt32Converter>(TrimmableTypeConverter.GetConverter(typeof(uint)));
Assert.IsType<Int64Converter>(TrimmableTypeConverter.GetConverter(typeof(long)));
Assert.IsType<UInt64Converter>(TrimmableTypeConverter.GetConverter(typeof(ulong)));
Assert.IsType<SingleConverter>(TrimmableTypeConverter.GetConverter(typeof(float)));
Assert.IsType<DoubleConverter>(TrimmableTypeConverter.GetConverter(typeof(double)));
Assert.IsType<DecimalConverter>(TrimmableTypeConverter.GetConverter(typeof(decimal)));
Assert.IsType<CharConverter>(TrimmableTypeConverter.GetConverter(typeof(char)));
Assert.IsType<StringConverter>(TrimmableTypeConverter.GetConverter(typeof(string)));
Assert.IsType<DateTimeConverter>(TrimmableTypeConverter.GetConverter(typeof(DateTime)));

Assert.IsType<MyConverter>(TrimmableTypeConverter.GetConverter(typeof(Converted)));
Assert.IsType<TypeConverter>(TrimmableTypeConverter.GetConverter(typeof(Unconverted)));

class Unconverted {}

[TypeConverter(typeof(MyConverter))]
class Converted
{
}

class MyConverter : TypeConverter { }