global using System.IO;
global using Xunit;

// WPF pack resources and native window state are process-wide. Running multiple
// STA window fixtures concurrently can race inside Application.LoadComponent.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
