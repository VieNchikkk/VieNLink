using Android.Content;
using Android.Database;
using Android.OS;

namespace VieNLink.Mobile;

/// <summary>
/// Отдаёт скачанный APK системному установщику по content:// — без доступа
/// к общей памяти и без FileProvider из AndroidX. Файл лежит в cache/updates,
/// доступ выдаётся только на один Intent через FLAG_GRANT_READ_URI_PERMISSION.
/// </summary>
[ContentProvider(
    new[] { UpdateFileProvider.Authority },
    Name = "VieNLink.Mobile.UpdateFileProvider",
    Exported = false,
    GrantUriPermissions = true)]
public sealed class UpdateFileProvider : ContentProvider
{
    public const string Authority = "com.vienlink.app.updates";

    public override bool OnCreate() => true;

    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        if (!string.Equals(mode, "r", StringComparison.Ordinal))
        {
            throw new Java.IO.FileNotFoundException("APK открывается только для чтения.");
        }

        var name = Path.GetFileName(uri.LastPathSegment ?? string.Empty);
        var directory = new Java.IO.File(Context!.CacheDir, "updates");
        var file = new Java.IO.File(directory, name);
        if (string.IsNullOrWhiteSpace(name) || !file.Exists())
        {
            throw new Java.IO.FileNotFoundException("APK обновления не найден.");
        }

        return ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly);
    }

    public override string? GetType(Android.Net.Uri uri) => "application/vnd.android.package-archive";

    public override ICursor? Query(
        Android.Net.Uri uri,
        string[]? projection,
        string? selection,
        string[]? selectionArgs,
        string? sortOrder) => null;

    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => null;

    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;

    public override int Update(
        Android.Net.Uri uri,
        ContentValues? values,
        string? selection,
        string[]? selectionArgs) => 0;
}
