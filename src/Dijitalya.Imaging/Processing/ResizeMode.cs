namespace Dijitalya.Imaging.Processing;

/// <summary>How a picture is fitted into a box of a given width and height. Proportions are kept in every mode.</summary>
public enum ResizeMode
{
    /// <summary>
    /// As large as fits inside the box: the result may be narrower or shorter than the box, and
    /// nothing is cut off. A width or height of 0 is worked out from the other.
    /// </summary>
    Max = 0,

    /// <summary>Fills the box and cuts off what overflows, keeping the centre: the result is exactly the box.</summary>
    Crop = 1,

    /// <summary>Fits inside the box and fills the rest with transparency, centred: the result is exactly the box.</summary>
    Pad = 2,
}
