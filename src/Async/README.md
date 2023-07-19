# StaticCS.Async

StaticCS.Async is a helper library for async code providing support for things like Structured Concurrency.

Structured Concurrency is a programming style where concurrent Tasks are always started and completed in nested scopes, meaning that no nested Tasks escape their parent scope. This support is provided by the TaskScope class. Unlike conventional C# concurrency using Task.Run, structured concurrency has clear lifetime demarcations and reduces the risk of unobserved exceptions in background tasks.