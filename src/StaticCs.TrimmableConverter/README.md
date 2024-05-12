
# StaticCs.TrimmableConverter

A trim- and AOT-compatible version of TypeDescriptor.GetConverter. Unlike that API, TrimmableTypeConverter.GetConverter does not respect `ICustomTypeDescriptor`. TrimmableTypeConverter only returns TypeConverters for intrinsic types, or converters specified using the `TypeConverterAttribute`.