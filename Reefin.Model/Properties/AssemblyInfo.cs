using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("Reefin.Model")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Reefin Project")]
[assembly: AssemblyProduct("Reefin Server")]
[assembly: AssemblyCopyright("Copyright ©  2019 Reefin Contributors. Code released under the GNU General Public License")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: NeutralResourcesLanguage("en")]
[assembly: InternalsVisibleTo("Reefin.Model.Tests")]

// PR115b (docs/pr115-design-canary-execution.md §3.C): PlaybackExecutionPlanAdapter reuses
// StreamBuilder's internal TranscodingProfile-matching and RequireAvc/RequireNonAnamorphic
// resolution logic rather than duplicating it (single source of truth, see that method's remarks).
[assembly: InternalsVisibleTo("Reefin.Playback.Dlna")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
