using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// When the pictures of players that came from another graphics adapter are believed, and what
/// the engine reads out of a texture to decide it.
/// </summary>
public sealed class StudioPreviewProofTests
{
    private const ulong Nothing = StudioPreviewPixels.Nothing;

    [Fact]
    public void Proof_HoldsWhenTwoRoundsInARowLeaveTheSamePictures()
    {
        var proof = new StudioPreviewProof();

        Assert.False(proof.Offer([0xA1, 0xB2]));
        Assert.True(proof.Offer([0xA1, 0xB2]));
        Assert.Equal(2, proof.Rounds);
    }

    [Fact]
    public void Proof_OneRoundIsNeverEnough()
    {
        Assert.False(new StudioPreviewProof().Offer([0xA1]));
    }

    [Fact]
    public void Proof_APictureThatChanged_StartsTheComparisonAgain()
    {
        // The screen came back black once, which is what a player that has just moved does, and
        // then a frame ahead of its position.
        var proof = new StudioPreviewProof();

        Assert.False(proof.Offer([0xB1AC, 0xC0]));
        Assert.False(proof.Offer([0xF1, 0xC0]));
        Assert.False(proof.Offer([0xF0, 0xC0]));
        Assert.True(proof.Offer([0xF0, 0xC0]));
        Assert.Equal(4, proof.Rounds);
    }

    [Fact]
    public void Proof_ATextureWithNothingInIt_IsNeverBelieved_HoweverOftenItComesBack()
    {
        var proof = new StudioPreviewProof();

        Assert.False(proof.Offer([Nothing, 0xC0]));
        Assert.False(proof.Offer([Nothing, 0xC0]));
        Assert.False(proof.Offer([Nothing, 0xC0]));

        // The round before had nothing in it, so this one is the first of two.
        Assert.False(proof.Offer([0xF0, 0xC0]));
        Assert.True(proof.Offer([0xF0, 0xC0]));
    }

    [Fact]
    public void Proof_NoPicturesAtAll_ProveNothing_AndAnotherNumberOfThemIsAnotherPicture()
    {
        var proof = new StudioPreviewProof();

        Assert.False(proof.Offer([]));
        Assert.False(proof.Offer([]));
        Assert.False(proof.Offer([0xF0]));
        Assert.False(proof.Offer([0xF0, 0xC0]));
        Assert.True(proof.Offer([0xF0, 0xC0]));
    }

    [Fact]
    public void Proof_GoesOnComparing_AfterItHeld()
    {
        var proof = new StudioPreviewProof();
        proof.Offer([0xF0]);
        Assert.True(proof.Offer([0xF0]));

        Assert.False(proof.Offer([0xF1]));
        Assert.True(proof.Offer([0xF1]));
    }

    [Fact]
    public void Pixels_AreEmpty_WhenEveryOneIsTransparent_WhateverItsColour()
    {
        Assert.True(StudioPreviewPixels.IsEmpty([0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(StudioPreviewPixels.IsEmpty([30, 200, 10, 0, 1, 2, 3, 0]));
        Assert.True(StudioPreviewPixels.IsEmpty([]));

        // A frame of video is opaque, however dark: black is a picture.
        Assert.False(StudioPreviewPixels.IsEmpty([0, 0, 0, 255, 0, 0, 0, 255]));
        Assert.False(StudioPreviewPixels.IsEmpty([0, 0, 0, 0, 0, 0, 0, 1]));
    }

    [Fact]
    public void Fingerprint_IsNothing_ForTransparentPixelsOnly()
    {
        Assert.Equal(Nothing, StudioPreviewPixels.Fingerprint([0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(Nothing, StudioPreviewPixels.Fingerprint([9, 9, 9, 0]));
        Assert.NotEqual(Nothing, StudioPreviewPixels.Fingerprint([0, 0, 0, 255]));
    }

    [Fact]
    public void Fingerprint_IsTheSameForTheSamePicture_AndAnotherForAnother()
    {
        var picture = new byte[64 * 4];
        for (var index = 0; index < picture.Length; index++)
        {
            picture[index] = index % 4 == 3 ? (byte)255 : (byte)(index * 7);
        }

        var same = (byte[])picture.Clone();
        var onePixelOff = (byte[])picture.Clone();
        onePixelOff[40] ^= 1;
        var black = new byte[64 * 4];
        for (var index = 3; index < black.Length; index += 4)
        {
            black[index] = 255;
        }

        Assert.Equal(StudioPreviewPixels.Fingerprint(picture), StudioPreviewPixels.Fingerprint(same));
        Assert.NotEqual(StudioPreviewPixels.Fingerprint(picture), StudioPreviewPixels.Fingerprint(onePixelOff));
        Assert.NotEqual(StudioPreviewPixels.Fingerprint(picture), StudioPreviewPixels.Fingerprint(black));

        // The same pixels in another order are another picture.
        var swapped = (byte[])picture.Clone();
        (swapped[0], swapped[4]) = (swapped[4], swapped[0]);
        Assert.NotEqual(StudioPreviewPixels.Fingerprint(picture), StudioPreviewPixels.Fingerprint(swapped));
    }
}
