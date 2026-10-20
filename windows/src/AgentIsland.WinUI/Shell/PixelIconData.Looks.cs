using AgentIsland.Model;

namespace AgentIsland.WinUI.Shell;

// Drawn for Agent Island's pixel kit and shipped under the repository's
// licence: the faces of the looks other than each agent's default, 10 x 10
// cells like FaceGrids (PixelIconData.cs), from the pixel characters' release.
internal static partial class PixelIcons
{
    // Faces for the looks other than each agent's default (v5.2). The default
    // looks keep their FaceGrids entries.
    private static readonly Dictionary<(DisplayProvider, string), (string[] Rows, (char Key, uint Rgb)[] Colors)> LookFaceGrids = new()
    {
        [(DisplayProvider.Codex, "puff")] = (new[]
        {
            "..........",
            "....###...",
            "...#www#..",
            ".##wwwww#.",
            "#wwwwwwww#",
            "#wwkwwkww#",
            "#wwkwwkww#",
            "#ssssssss#",
            ".########.",
            "..........",
        }, new[] { ('#', 0x7D808Bu), ('w', 0xF6F7F9u), ('k', 0x2E3038u), ('s', 0xE2E4EAu) }),
        [(DisplayProvider.Codex, "cloud")] = (new[]
        {
            "..........",
            "...####...",
            ".##wwww##.",
            "#wwwwwwww#",
            "#wwkwwkww#",
            "wwwkwwkwww",
            "#ssssssss#",
            ".########.",
            "..........",
            "..........",
        }, new[] { ('#', 0x7A7D88u), ('w', 0xECEDF1u), ('k', 0x26272Du), ('s', 0xCDD0D8u) }),
        [(DisplayProvider.Codex, "folio")] = (new[]
        {
            "..bB......",
            ".#bB###g..",
            ".#wwwwwgg.",
            ".#wwwwwww#",
            ".#wkwwkww#",
            ".#wkwwkww#",
            ".#wwkkwww#",
            ".#wwwwwww#",
            ".#########",
            "..f...f...",
        }, new[] { ('b', 0x4D96EBu), ('B', 0x3166A4u), ('#', 0x263349u), ('g', 0xF2BB55u), ('w', 0xFFF2D8u), ('k', 0x263349u), ('f', 0xD8C9ADu) }),
        [(DisplayProvider.Codex, "scroll")] = (new[]
        {
            "..........",
            ".gr....rg.",
            ".rRwwwwrR.",
            ".rRwwwwrR.",
            ".rRkwwkrR.",
            ".rRkwwkrR.",
            ".rRwwwwrR.",
            ".rRwwwwrR.",
            ".gr....rg.",
            "..........",
        }, new[] { ('g', 0xF2BB55u), ('r', 0xEFD9AFu), ('R', 0xC09A5Cu), ('w', 0xFFF2D8u), ('k', 0x263349u) }),
        [(DisplayProvider.Antigravity, "rainbow")] = (new[]
        {
            "....rr....",
            "...rrrr...",
            "..oooooo..",
            "..yyyyyy..",
            ".gkggggkg.",
            ".gkggggkg.",
            "bbbbbbbbbb",
            "bbb....bbb",
            "vv......vv",
            "..........",
        }, new[] { ('r', 0xF2483Bu), ('o', 0xF2832Bu), ('y', 0xF5BE2Eu), ('g', 0x4DB86Bu), ('k', 0x1E2033u), ('b', 0x2FA3D6u), ('v', 0x3D7BF5u) }),
        [(DisplayProvider.Antigravity, "aeri")] = (new[]
        {
            "....bb...y",
            "...bbbb...",
            "...b..b...",
            "..bffffb..",
            "g.fkffkf..",
            ".bffffffb.",
            ".bb....bbc",
            "bb......bb",
            "b........b",
            "..........",
        }, new[] { ('b', 0x4D96EBu), ('y', 0xF2BB55u), ('f', 0xFFF2D8u), ('g', 0x56AB78u), ('k', 0x263349u), ('c', 0xE77C65u) }),
        [(DisplayProvider.Antigravity, "blocks")] = (new[]
        {
            "..bbbbbb..",
            "..fkffkf..",
            "..fkffkf..",
            "..BBBBBB..",
            "..........",
            ".cc....gg.",
            ".cc.yy.gg.",
            "cc..yy..gg",
            "cc......gg",
            "..........",
        }, new[] { ('b', 0x4D96EBu), ('f', 0xFFF2D8u), ('k', 0x263349u), ('B', 0x3166A4u), ('c', 0xE77C65u), ('g', 0x56AB78u), ('y', 0xF2BB55u) }),
        [(DisplayProvider.Antigravity, "kite")] = (new[]
        {
            "....lb....",
            "...llbb...",
            "..lllbbb..",
            ".ssssssss.",
            ".llkbbkbb.",
            "..llbbbb..",
            "...lbbb...",
            "....lb....",
            "....c.....",
            ".....y....",
        }, new[] { ('l', 0x7DB6F5u), ('b', 0x4D96EBu), ('s', 0xFFF2D8u), ('k', 0x263349u), ('c', 0xE77C65u), ('y', 0xF2BB55u) }),
        [(DisplayProvider.Grok, "ringbot")] = (new[]
        {
            ".......kk.",
            "......k...",
            "...kkkk...",
            "..kwwwwk..",
            ".kwkwwkwk.",
            "kkwkwwkwkk",
            ".kwwwwwwk.",
            "..kwwwwk..",
            "...kkkk...",
            "...k..k...",
        }, new[] { ('k', 0x0B0B0Cu), ('w', 0xEFEFECu) }),
        [(DisplayProvider.Grok, "horizon")] = (new[]
        {
            "..........",
            "...kkkk...",
            "..kkkkkk.o",
            ".kkwkkwkoo",
            ".kkwkkwooy",
            ".kkkkoooyk",
            ".kkooyykk.",
            ".ooyykkk..",
            "oo.kkkk...",
            "..........",
        }, new[] { ('k', 0x0B0B0Cu), ('o', 0xF2932Bu), ('w', 0xFFFFFFu), ('y', 0xFFD66Bu) }),
        [(DisplayProvider.Grok, "cat")] = (new[]
        {
            ".k......k.",
            ".kek..kek.",
            ".kkkkkkkk.",
            ".kgkkkkgk.",
            ".kgkkkkgk.",
            ".kkkkpkkk.",
            "..kkkkkk..",
            "..kkkkwk.k",
            "..kkkwkkk.",
            "..ww..ww..",
        }, new[] { ('k', 0x0B0B0Cu), ('e', 0x3A3A42u), ('g', 0xF5BE2Eu), ('p', 0xE8849Au), ('w', 0xF4F4F1u) }),
        [(DisplayProvider.Grok, "astronaut")] = (new[]
        {
            "...oooo...",
            "..owwwwo..",
            ".owvvvvwo.",
            ".ovgvvvvo.",
            ".ovevvevo.",
            ".owvvvvwo.",
            "..owwwwo..",
            ".oowwwwoo.",
            "..owwwwo..",
            "..oo..oo..",
        }, new[] { ('o', 0x0B0B0Cu), ('w', 0xF4F4F1u), ('v', 0x16161Au), ('g', 0x8D8C94u), ('e', 0xFFFFFFu) }),
        [(DisplayProvider.Grok, "martian")] = (new[]
        {
            "..g....g..",
            "...o..o...",
            ".oooooooo.",
            "offffffffo",
            "ofkkffkkfo",
            "ofkkffkkfo",
            ".offffffo.",
            "...oooo...",
            ".oooooooo.",
            "...o..o...",
        }, new[] { ('g', 0xF5BE2Eu), ('o', 0x0B0B0Cu), ('f', 0xF4F4F1u), ('k', 0x0B0B0Cu) }),
        [(DisplayProvider.Kimi, "rabbit")] = (new[]
        {
            ".oo....oo.",
            ".ob....bo.",
            ".ob....bo.",
            ".owoooowo.",
            "owwwwwwwwo",
            "owkwwwwkwo",
            "owwwppwwwo",
            ".owwbbwwo.",
            ".owbbbbwo.",
            "..oooooo..",
        }, new[] { ('o', 0x0B2C55u), ('b', 0x2B8BFFu), ('w', 0xFFFFFFu), ('k', 0x0B2C55u), ('p', 0xFF7FA8u) }),
        [(DisplayProvider.Kimi, "terminal")] = (new[]
        {
            "......d...",
            "......a...",
            ".llllllll.",
            ".bnnnnnnb.",
            ".bnwnnwnb.",
            ".bnwnnwnb.",
            ".bnnnnnnb.",
            ".bbbbbbgb.",
            "..n....n..",
            ".nn....nn.",
        }, new[] { ('d', 0x0B2C55u), ('a', 0x0B2C55u), ('l', 0x7DB8FFu), ('b', 0x2B8BFFu), ('n', 0x0B2C55u), ('w', 0xFFFFFFu), ('g', 0x5BE37Du) }),
        [(DisplayProvider.Kimi, "moonlet")] = (new[]
        {
            "..ss......",
            ".sbb......",
            "sbbb...dd.",
            "sbbb...dd.",
            "bbbbb.....",
            "bbbbbbb..b",
            "bwbbwbbbbb",
            "bwbbwbbbbb",
            ".pbbbbbpb.",
            "..DDDDDD..",
        }, new[] { ('s', 0x7DB8FFu), ('b', 0x2B8BFFu), ('d', 0x0B2C55u), ('w', 0xFFFFFFu), ('p', 0xFF9EC4u), ('D', 0x1F6FD6u) }),
        [(DisplayProvider.Kimi, "rocket")] = (new[]
        {
            "....nn....",
            "...nnnn...",
            "..lbbbbb..",
            "..lrwwrb..",
            "..rkwwkr..",
            "..lrwwrb..",
            "..nnnnnn..",
            ".nlbbbbbn.",
            "nn.ssss.nn",
            "..........",
        }, new[] { ('n', 0x0B2C55u), ('l', 0x7DB8FFu), ('b', 0x2B8BFFu), ('r', 0xCFE6FFu), ('w', 0xFFFFFFu), ('k', 0x0B2C55u), ('s', 0x8D94A6u) }),
    };
}
