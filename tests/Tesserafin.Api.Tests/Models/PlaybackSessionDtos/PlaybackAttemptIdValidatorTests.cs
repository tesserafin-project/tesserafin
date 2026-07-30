using System;
using System.Collections.Generic;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Xunit;

namespace Tesserafin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Issue #43: the attempt id is <b>opaque</b> and <b>optional</b>. These tests pin both — that no
/// structure is imposed on a supplied value, and that omitting it is always valid.
/// </summary>
public class PlaybackAttemptIdValidatorTests
{
    [Fact]
    public void Validate_Null_IsAccepted()
    {
        var errors = new List<string>();
        PlaybackAttemptIdValidator.Validate(null, errors);
        Assert.Empty(errors);
    }

    /// <summary>
    /// Opacity, stated as a test: the server imposes no structure at all. Every one of these is a
    /// legitimate client scheme and none may be rejected for its shape.
    /// </summary>
    /// <param name="value">Client-supplied attempt id whose shape the validator must not constrain.</param>
    [Theory]
    [InlineData("a")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("attempt:42/retry")]
    [InlineData("не-ascii-и-это-нормально")]
    [InlineData("{\"looks\":\"like json\"}")]
    [InlineData("has spaces in the middle")]
    public void Validate_ImposesNoStructureOnASuppliedValue(string value)
    {
        var errors = new List<string>();
        PlaybackAttemptIdValidator.Validate(value, errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AtExactlyMaxLength_IsAccepted()
    {
        var errors = new List<string>();
        PlaybackAttemptIdValidator.Validate(new string('x', PlaybackAttemptIdValidator.MaxLength), errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_OverMaxLength_IsRejected()
    {
        var errors = new List<string>();
        PlaybackAttemptIdValidator.Validate(new string('x', PlaybackAttemptIdValidator.MaxLength + 1), errors);
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyOrWhitespace_IsRejectedRatherThanTreatedAsOmitted(string value)
    {
        // Silently accepting a blank would create one attempt bucket merging every unrelated
        // attempt that also sent a blank - worse than no correlation at all.
        var errors = new List<string>();
        PlaybackAttemptIdValidator.Validate(value, errors);
        Assert.Single(errors);
    }

    [Fact]
    public void Validate_ControlCharacters_AreRejected()
    {
        var errors = new List<string>();
        PlaybackAttemptIdValidator.Validate("attempt\n42", errors);
        Assert.Single(errors);
    }

    [Fact]
    public void ValidateOrThrow_Null_DoesNotThrow()
    {
        // The AC in full: a third-party client that never sends the field is entirely unaffected.
        PlaybackAttemptIdValidator.ValidateOrThrow(null);
    }

    [Fact]
    public void ValidateOrThrow_MalformedValue_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => PlaybackAttemptIdValidator.ValidateOrThrow(new string('x', PlaybackAttemptIdValidator.MaxLength + 1)));
}
