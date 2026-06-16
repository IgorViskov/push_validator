using System.ComponentModel;

namespace LLMAgent.Extensions;

public static class EnumExtensions
{
    public static string GetDescription(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        if (field == null) return value.ToString();

        var descriptionAttributes = field.GetCustomAttributes(typeof(DescriptionAttribute), false);
        if (descriptionAttributes.Length > 0)
        {
            return ((DescriptionAttribute)descriptionAttributes[0]).Description;
        }

        return value.ToString();
    }
}