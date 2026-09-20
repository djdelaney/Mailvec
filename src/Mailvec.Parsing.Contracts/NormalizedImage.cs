namespace Mailvec.Pdf;

/// <summary>
/// Decoded SOURCE dimensions (pre-downscale — what the OCR dimension / aspect
/// gate keys off) plus the normalised OCR-ready JPEG bytes. Produced by
/// <c>ImageRenderer.TryNormalize</c>; crosses the parser boundary, hence a
/// Contracts type. Keeps the <c>Mailvec.Pdf</c> namespace it was born with.
/// </summary>
public sealed record NormalizedImage(int Width, int Height, byte[] Jpeg);
