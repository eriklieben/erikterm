using ErikTerm.App.Native;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Variable fonts: a family that ships one file with named instances (Cascadia Code on Windows 11, Adwaita Sans,
/// many "[wght]" fonts) has no separate Bold/Italic files, so the locator returns the regular file for every style.
/// Such a style gets its own face on the same file, switched to the matching named instance with
/// FT_Set_Named_Instance (see <see cref="VariableInstances.Pick"/>).
/// </summary>
#pragma warning disable S6640 // FreeType's variable-font API uses out-pointers
internal sealed unsafe partial class FontSet
#pragma warning restore S6640
{
    private readonly Dictionary<string, List<VariableInstance>> _instances = []; // per file, during construction

    /// <summary>The named instance of <paramref name="path"/> for <paramref name="style"/>, or 0 (static font / none fits).</summary>
    private int VariableInstanceFor(string path, FontStyle style)
    {
        if (!_instances.TryGetValue(path, out var list))
        {
            list = [];
            nint h;
            var (file, faceIndex) = FontFiles.Split(path);
            if (FreeType.NewFace(_library, file, faceIndex, &h) == 0)
            {
                list = FreeType.NamedInstances(_library, h);
                FreeType.DoneFace(h);
            }
            _instances[path] = list;
        }
        return VariableInstances.Pick(list, style);
    }
}
