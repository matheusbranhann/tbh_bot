using TbhBot.Core.Game;
using TbhBot.Core.Il2Cpp;

namespace TbhBot.Tests;

public class CoreTests
{
    // (1) ObscuredValue: EncodeHidden -> Decode devolve o value original (round-trip),
    // pra varias combinacoes value/key, incluindo negativos e o 4310 (max stage).
    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(4310, 0u)]
    [InlineData(4310, 0xDEADBEEFu)]
    [InlineData(-1, 0u)]
    [InlineData(-1, 0xFFFFFFFFu)]
    [InlineData(-4310, 12345u)]
    [InlineData(int.MaxValue, 0x1u)]
    [InlineData(int.MinValue, 0x7FFFFFFFu)]
    [InlineData(123456789, 0xABCDEF01u)]
    [InlineData(-987654321, 0x13579BDFu)]
    public void ObscuredValue_EncodeDecode_RoundTrips(int value, uint key)
    {
        uint hidden = ObscuredValue.EncodeHidden(value, key);
        Assert.Equal(value, ObscuredValue.Decode(hidden, key));
    }

    // (2) GameConstants: 25 stats, 18 campos de fase, e o build 2c430296063a com Gra==0xC1F730.
    [Fact]
    public void GameConstants_Tables_HaveExpectedShape()
    {
        Assert.Equal(25, GameConstants.Stats.Count);
        Assert.Equal(18, GameConstants.StageFields.Count);

        Assert.True(GameConstants.KnownBuilds.ContainsKey("2c430296063a"));
        Assert.Equal(0xC1F730, GameConstants.KnownBuilds["2c430296063a"].Gra);
    }

    // (3) SymbolTable.LoadKnownBuild popula o dict a partir do build conhecido.
    [Fact]
    public void SymbolTable_LoadKnownBuild_PopulatesOffsets()
    {
        var sym = new SymbolTable();

        Assert.True(sym.LoadKnownBuild("2c430296063a"));
        Assert.Equal(0x5DD2A30, sym.Get("cube_slot"));
        Assert.Equal(0x6F65F0, sym.Ynj[0]);
    }

    // (4) SymbolTable.LoadOffsetsJson le o cache no formato do Python (dict plano + ynj + inv_class).
    [Fact]
    public void SymbolTable_LoadOffsetsJson_ReadsFlatCache()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tbh_offsets_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"gra":123,"ynj":[9],"inv_class":"x","uo_max":80}""");

        try
        {
            var sym = new SymbolTable();
            Assert.True(sym.LoadOffsetsJson(path));
            Assert.Equal(123, sym.Get("gra"));
            Assert.Equal(9, sym.Ynj[0]);
            Assert.Equal("x", sym.InvClass);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
