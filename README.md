# static-cs

A series of analyzers, attributes, and libraries to help C# developers write clearer, more type-safe code.

## Features

### [Closed] attribute

The [Closed] attribute is used to simulate discriminated unions or "complete" enums. It can be applied to enums, classes, or records that meet certain requirements. The effect is that when you use a `Closed` type as the argument to a `switch` expression, if all cases are checked inside the arms of the switch, the "incompleteness" warning that is normally produced by the C# compiler will be suppressed.

## Subpackages

### StaticCS.Async

A library for structured concurrency in C#. See [the Async README.md](src/Async/README.md) for more info.
