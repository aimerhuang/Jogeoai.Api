namespace Jogeoai.Infrastructure;

internal static class KnowledgeFolderHierarchy
{
    internal static async Task<bool> WouldCreateCycleAsync(Guid folderId, Guid proposedParentId,
        Func<Guid, CancellationToken, Task<Guid?>> readParentAsync, CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<Guid>();
        Guid? ancestorId = proposedParentId;
        while (ancestorId is Guid currentId)
        {
            if (currentId == folderId || !visited.Add(currentId)) return true;
            ancestorId = await readParentAsync(currentId, cancellationToken);
        }
        return false;
    }
}
