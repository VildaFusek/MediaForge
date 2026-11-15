using System;

namespace MediaForge.Services;

// Service-to-service progress stays synchronous; only the UI boundary posts callbacks.
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
