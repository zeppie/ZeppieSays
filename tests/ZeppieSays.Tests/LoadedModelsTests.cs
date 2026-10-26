using ZeppieSays.Llm;
using ZeppieSays.Services;

namespace ZeppieSays.Tests;

public class LoadedModelsTests
{
    [Fact]
    public void Reads_nvidia_smi_memory_in_mebibytes()
    {
        var usage = GpuMemory.Parse("6144, 8192\r\n");
        Assert.Equal(6144L * 1024 * 1024, usage?.UsedBytes);
        Assert.Equal(8192L * 1024 * 1024, usage?.TotalBytes);
    }

    [Fact]
    public void Uses_the_first_graphics_card()
    {
        Assert.Equal(8192L * 1024 * 1024, GpuMemory.Parse("100, 8192\n200, 24576\n")?.TotalBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No devices were found")]
    [InlineData("10, 0")]
    public void Ignores_output_that_is_not_a_memory_reading(string output)
    {
        Assert.Null(GpuMemory.Parse(output));
    }

    [Fact]
    public void A_model_kept_forever_expires_centuries_from_now()
    {
        var forever = new OllamaLoadedModel("qwen3:4b", 1, 1, DateTimeOffset.Now.AddYears(300));
        var soon = new OllamaLoadedModel("qwen3:4b", 1, 1, DateTimeOffset.Now.AddMinutes(30));
        Assert.True(forever.StaysLoaded);
        Assert.False(soon.StaysLoaded);
    }
}
