using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// Issue #75's two "the vocabulary stays closed and in step with the contract" guards, distinct from
/// the type-closure walk in <see cref="ContractClosureTests"/>:
/// <list type="bullet">
/// <item><description>
/// no diagnostic enum may grow an open-ended escape hatch (an <c>Unknown</c>/<c>Other</c> member),
/// because such a member is exactly how a "we saw something we cannot name" signal would sneak back
/// in after issue #75 ruled it out;
/// </description></item>
/// <item><description>
/// every member of the request contract the diagnostic compares must have a server-owned
/// <see cref="ContractPath"/>, so that adding a contract property without teaching the diagnostic
/// about it cannot pass silently.
/// </description></item>
/// </list>
/// </summary>
public sealed class ContractVocabularyClosedTests
{
    private static readonly Assembly _contractAssembly = typeof(ContractMappingDiagnostic).Assembly;

    // Names that would re-open a closed vocabulary: a catch-all bucket a value the server could not
    // classify would be filed under. Issue #75 forbids all of them.
    private static readonly string[] _openEndedNames =
    [
        "Unknown",
        "Other",
        "Unspecified",
        "Extension",
        "Custom",
        "Misc",
        "Extra",
        "Catchall",
    ];

    /// <summary>
    /// <see cref="ContractIssueCode"/> is exactly the closed set issue #75 defined - no more, no
    /// fewer. Adding any member (an <c>Unknown</c> above all) turns this red.
    /// </summary>
    [Fact]
    public void ContractIssueCode_IsExactlyTheClosedSet()
    {
        Assert.Equal(
            new[] { "None", "Missing", "WrongType", "OutOfRange", "UnsupportedValue", "Truncated" },
            Enum.GetNames<ContractIssueCode>());
    }

    /// <summary>
    /// <see cref="ContractMember"/> is exactly the closed set of server-owned contract segments.
    /// Adding any member turns this red.
    /// </summary>
    [Fact]
    public void ContractMember_IsExactlyTheClosedSet()
    {
        Assert.Equal(
            new[]
            {
                "None",
                "Capabilities",
                "Decode",
                "DirectPlayProfiles",
                "VideoCodecs",
                "AudioCodecs",
                "SubtitleDelivery",
                "SupportsHls",
                "SupportsDash",
                "OutputProfiles",
                // Issue #75 slice 75b: the request-root container segment the structural scan
                // attributes a top-level unknown member to. Still a closed, server-owned segment.
                "Request",
            },
            Enum.GetNames<ContractMember>());
    }

    /// <summary>
    /// No enum anywhere in the diagnostic assembly carries an open-ended catch-all member. This is
    /// the assembly-wide form of the two exact-set tests above: it keeps holding if a new closed enum
    /// is added later, and it is the guard the "add an <c>Unknown</c> member" mutation trips.
    /// </summary>
    [Fact]
    public void NoDiagnosticEnum_HasAnOpenEndedMember()
    {
        var offenders = new List<string>();

        foreach (var enumType in _contractAssembly.GetTypes().Where(t => t.IsEnum))
        {
            foreach (var name in Enum.GetNames(enumType))
            {
                if (_openEndedNames.Any(open => string.Equals(name, open, StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add($"{enumType.Name}.{name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Issue #75: a diagnostic enum grew an open-ended catch-all member, which re-opens the closed vocabulary: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every member of the request contract the diagnostic actually compares - the public properties
    /// of the live <see cref="ClientCapabilities"/> and <see cref="DecodeCapabilities"/> domain
    /// types - has a matching <see cref="ContractMember"/> that some <see cref="ContractPath"/> names.
    /// Add a property to either domain type without extending the diagnostic vocabulary and this goes
    /// red, naming the orphaned property.
    /// </summary>
    [Fact]
    public void EveryComparedContractProperty_HasAContractPath()
    {
        var pathMembers = MembersNamedByAnyContractPath();
        var orphans = new List<string>();

        foreach (var declaringType in new[] { typeof(ClientCapabilities), typeof(DecodeCapabilities) })
        {
            foreach (var property in declaringType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!Enum.TryParse<ContractMember>(property.Name, out var member) || !pathMembers.Contains(member))
                {
                    orphans.Add($"{declaringType.Name}.{property.Name}");
                }
            }
        }

        Assert.True(
            orphans.Count == 0,
            "Issue #75: a compared contract property has no ContractMember named by any ContractPath - "
            + "add it to ContractMember and give it a ContractPath, or it is silently undiagnosable: "
            + string.Join(", ", orphans));
    }

    /// <summary>
    /// The set of <see cref="ContractMember"/> values that appear in at least one
    /// <see cref="ContractPath"/> exposed as a static property.
    /// </summary>
    private static HashSet<ContractMember> MembersNamedByAnyContractPath()
    {
        var members = new HashSet<ContractMember>();

        foreach (var property in typeof(ContractPath).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (property.PropertyType != typeof(ContractPath) || property.GetValue(null) is not ContractPath path)
            {
                continue;
            }

            members.Add(path.Root);
            members.Add(path.Branch);
            members.Add(path.Leaf);
        }

        return members;
    }
}
