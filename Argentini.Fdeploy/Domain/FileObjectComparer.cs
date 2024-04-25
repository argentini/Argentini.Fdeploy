namespace Argentini.Fdeploy.Domain;

public class FileObjectComparer : IEqualityComparer<FileObject>
{
    public bool Equals(FileObject? x, FileObject? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (ReferenceEquals(x, null) || ReferenceEquals(y, null)) return false;

        return x.RelativeComparablePath == y.RelativeComparablePath;
    }

    public int GetHashCode(FileObject obj)
    {
        return obj.RelativeComparablePath.GetHashCode();
    }
}
