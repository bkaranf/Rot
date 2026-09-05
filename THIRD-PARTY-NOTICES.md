# Third-party notices

Rot's MIT license applies to Rot source and original artwork. Bundled dependencies
retain their own licenses and notices, reproduced in [licenses](licenses/).

| Component | Distributed version | License and notices |
|---|---|---|
| .NET runtime | 10.0.11 | [License](licenses/dotnet-LICENSE.txt), [third-party notices](licenses/dotnet-THIRD-PARTY-NOTICES.txt) |
| .NET Windows Desktop runtime | 10.0.11 | [License](licenses/WindowsDesktop-LICENSE.txt) |
| Microsoft WebView2 SDK and loader | 1.0.4129.50 | [License](licenses/WebView2-LICENSE.txt), [notices](licenses/WebView2-NOTICE.txt) |

The WebView2 Evergreen browser runtime is installed separately by Microsoft and
updates independently. The YouTube website and official embedded player are remote
services and are not redistributed as Rot source. Their names and trademarks are
the property of their respective owners.

Development-only dependencies such as xUnit and Microsoft.NET.Test.Sdk are restored
through NuGet and are not included in the portable application package.
