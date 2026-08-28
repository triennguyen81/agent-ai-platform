using System.Security.Claims;

namespace Aiq.Gateway.Books;

public interface IBookAccessControl
{
    /// <summary>
    /// Authorises a user to mutate a specific book. Admins can always mutate;
    /// otherwise only the original uploader may write.
    /// </summary>
    bool CanMutate(ClaimsPrincipal user, BookRecord book);
}

public sealed class BookAccessControl : IBookAccessControl
{
    public bool CanMutate(ClaimsPrincipal user, BookRecord book)
    {
        if (user.IsInRole("admin") || user.IsInRole("tenant_owner"))
        {
            return true;
        }

        if (book.UploadedBy is null)
        {
            // Legacy / orphaned books — only admins may touch them.
            return false;
        }

        var sub = user.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var userId) && userId == book.UploadedBy.Value;
    }
}
