# Third-party notices

Stringline is licensed under the GNU General Public License, version 3 or later (see `LICENSE`). The published app also includes the components below, each under its own licence. All of them are compatible with the GPL. The About window (Help ▸ About Stringline) lists them as well.

| Component | Licence | Used for | Source |
|---|---|---|---|
| .NET, WPF and Microsoft.Extensions | MIT | runtime, windowing, hosting and logging | https://github.com/dotnet |
| SkiaSharp | MIT (Skia: BSD-3-Clause) | drawing the graph, PNG and PDF export | https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm | MIT | view models | https://github.com/CommunityToolkit/dotnet |
| Serilog | Apache-2.0 | log files | https://serilog.net |
| Microsoft.Data.Sqlite and SQLitePCLRaw | MIT and Apache-2.0 | saved sessions | https://github.com/dotnet/efcore |
| SQLite | Public domain | saved sessions | https://sqlite.org |
| Markdig | BSD-2-Clause | showing the user guide | https://github.com/xoofx/markdig |
| Google OR-Tools | Apache-2.0 | the re-planning optimiser | https://github.com/google/or-tools |
| Protocol Buffers, Abseil, RE2, SCIP, HiGHS, zlib and bzip2 | BSD-3-Clause, Apache-2.0, MIT, zlib and bzip2 | inside OR-Tools | https://github.com/google/or-tools/blob/stable/LICENSE |
| Noto Sans | SIL Open Font Licence 1.1 | the graph's labels | https://notofonts.github.io |

The Noto Sans fonts are embedded in `Stringline.Graph`. Their licence is in `src/Stringline.Graph/Fonts/OFL.txt` and is shipped with the app as `OFL.txt`.

OR-Tools ships its CP-SAT solver as native libraries for Windows, Linux and macOS, with the libraries it is built on (the second row); their licences are in the OR-Tools package and repository.

The `stringline` command-line tools also use System.CommandLine (MIT, https://github.com/dotnet/command-line-api).

The installer is built with Inno Setup (https://jrsoftware.org/isinfo.php), whose licence allows installers made with it to be distributed freely.
