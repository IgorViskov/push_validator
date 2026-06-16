namespace LLMAgent.Extensions;

public static class EnumerableExtensions
{
    public static IEnumerable<T> WhereSelectMany<T, U>(this IEnumerable<U> source, Func<U, IEnumerable<T>> selector, Func<T, bool> predicate)
    {
        foreach (var item in source)
        {
            foreach (var subItem in selector(item))
            {
                if (predicate(subItem))
                {
                    yield return subItem;
                }
            }
        }
    }
}