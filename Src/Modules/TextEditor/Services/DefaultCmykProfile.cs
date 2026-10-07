using System;
using System.IO;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Профиль печати для CMYK-картинок без собственного профиля.
    ///
    /// Картинка CMYK хранит доли краски, а не цвет: какой цвет даст краска, зависит
    /// от печати, и без профиля его не узнать. Word в этом случае берёт профиль
    /// печати по умолчанию. Здесь порядок такой же: профиль Windows для CMYK по
    /// умолчанию (RSWOP — офсет SWOP), а если его нет или система другая — свой,
    /// встроенный: компактный профиль CGATS001 (те же условия SWOP) из набора
    /// compact-icc-profiles, переданного в общественное достояние (CC0 1.0).
    /// </summary>
    internal static class DefaultCmykProfile
    {
        private static readonly Lazy<CmykColorProfile?> Instance = new(Load);

        /// <summary>Профиль по умолчанию; null — разобрать не удалось ни один.</summary>
        public static CmykColorProfile? Profile => Instance.Value;

        private static CmykColorProfile? Load()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string path = Path.Combine(system, "spool", "drivers", "color", "RSWOP.icm");
                    if (File.Exists(path))
                    {
                        var profile = CmykColorProfile.Parse(File.ReadAllBytes(path));
                        if (profile is not null) return profile;
                    }
                }
                catch (IOException)
                {
                    // Профиль системы недоступен — остаётся встроенный.
                }
                catch (UnauthorizedAccessException)
                {
                    // Профиль системы недоступен — остаётся встроенный.
                }
            }

            return CmykColorProfile.Parse(Convert.FromBase64String(EmbeddedCgats001));
        }

        /// <summary>CGATS001Compat-v2-micro.icc (compact-icc-profiles, CC0 1.0).</summary>
        private const string EmbeddedCgats001 =
            "AAAhEEFEQkUCEAAAc2NuckNNWUtMYWIgB+YADAAHABYAEAAqYWNzcE1TRlQAAAAAc2F3c2N0cmwAAAAAAAAAAAAAAAAAAPbWAAEA"
            + "AAAA0y1oYW5kw2fR0RrZabY+OZwwFrOlWAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEZGVzYwAAALQAAABfY3BydAAA"
            + "ANAAAAAMd3RwdAAAANwAAAAUQTJCMAAAAPAAACAgZGVzYwAAAAAAAAAFdUNNWQAAAAAAAAAAAAAAAHRleHQAAAAAQ0MwAFhZWiAA"
            + "AAAAAAD21gABAAAAANMtbWZ0MgAAAAAEAwYAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAADAAAgAACiMQABab"
            + "HJYjCCklL0Q1UztaQVpHTk1KUzBZGV71ZLxqhXArdcF7T4C7hheLa5Cmlc6a9KAVpR6qKK8ztEC5R75Rw1vIZ81k0lXXQdwh4Pzl"
            + "vupX7trzivgX/Gj//wAACNYNzhNiGIEeBiNNKKAuADNWOME+KUOlSSVOolQzWa9fLmStagpva3S/efR/JIRSiWKOb5N/mJOdl6Ks"
            + "p9OtDrJYt7q9NMLHyFrN9NOa2UjfAeSS6hPv1/WE+wf//wAACR4OLBPIGN4eYyOWKMgt9jMjOFQ9ikLRSCFNflLqWFld02NKaL5u"
            + "L3OUeO1+O4N+iLSN55MdmFqdqaMSqJ6uT7QpuifARcZyzKTSx9jJ3qHkSemg7qrz9Pi6/OH//wAACIcNORJzFzwceyFqJl4rXDBj"
            + "NW46hT+jRMpJ9E8jVE1ZbV6LY55oqm2wcqx3nHyHgXCGSosjj/6U35nJnsKj0aj7rkOzo7kkvsrEkcp90IXWptzw413qdvHB+Sj/"
            + "//8AgACAANZuf7V++61Wf5Z+WIM1f7p+U1ZOgDJ+wB9kgf6AHPt4fauSaNP3fZuOoasufcKLaoEgfiWI3VQ5fsSG5hyqgD6GY/hn"
            + "e+2lC9FMe/melKjRfESYwH8WfNWT2lJufYuPdBpJfpGMavXTesm3+87Tes2uzaageyOmhX0qe8ifNFDZfI2X8xhSfQqQBPPHegnL"
            + "YczPefe/X6TNekq0qXuzewGrRk+Qe8mjnxbIe7+PZ/IqeYzfhMsyeWLQfaNJeanDbHpzemu4Vk6WeziuYBWeesiOnePpjqh9Ob/Q"
            + "jDZ8rZrMih58jXQyiHx890pUh3l9xxQiibF/7ODvjKyN5r3BikmK45jxiFuIdHJXhu6Gq0iBhhKFjxIth8qFpd5siw2fCLtYiMiZ"
            + "s5bAhvWU+3CBha2RGEb5hOWOIhBhhhuJ69w6ifWwXbk5h7ioy5TMhe+h7m7UhKqb80Wpg+uXqQ8JhKWKK9pxiS3CLreJhvK4V5M/"
            + "hSuvX22Qg/OnpkShgymhpg4Hg4eJfNkMiJjU5bY1hmPIpJH6hJi9mWyFg2e0m0PRgqWqIQ1EgreI+MnVnet7Lql5mXt66og1lXV7"
            + "E2UMkg57wT4Nj7981glUkMx/u8dUnEmKa6fNl8aIB4a5k9mGKmOWkJmE9jynjmKEhwhxjn6EJMVxmsqZ7aXrlmKVe4T3kneRpWIE"
            + "j1KOpztdjSqMEwe2jJ6FN8OmmcGp5aQylV6jhoNCkYed2WCVjlyZGjpRjDOWTwcZiymEzMIqmQq6MKLWlKax7IH9kNKqe1+Kja+k"
            + "dDmLi26fDQauihWEhMD/mHvLaqHGlBnBLID1kEi4CV6zjS2xZzjrivSlZAZZiU2ES7ElrfV6ApPzp6l50XXyoc56F1XrnLR64TFY"
            + "lsl8AgDvlVx/w685rI2H7ZLKphmFs3TgoEOEQlTSmzyDeDBdmAuDwQDik2CAha3KqzqWE5FPpNmSRHN2nwKPHVOamgWM2S98lsmM"
            + "NgDOkbaAi6yRqjSkhZATo9ufDXI6ngWaWVKPmPGWxi7KlaiU6wDZkFWAk6tqqYKziY8goxSsVXFQnT+ioFHKmD6h+C5JlNSbiwDb"
            + "j0iAlKp3qQHDVI5XooO6W3CXnLOy8lE6l7quFS3slFKfDQDejoaAlpoxvt15T3/GtsN5BmSIrzZ5SkcqqJJ6FyQtpWN7DgAAj5mA"
            + "AJj5vXmGFn8NtUiES2PTraWC60ZzpvSCbyPKo3yC7wAAjtWAAJguvBWS/H4qs+6PlGLwrECM70W2pY+LWiN2oeKK7wAAjiGAAJdf"
            + "uxSct31pst6Y+GI1qyyULEUipGaRdyM8oI2P5AAAjZaAAJa6ukGtwnzrsfqnbGHNqkaihUTSo4Of0SMhn2qWQwAAjTmAAJYKubC8"
            + "RHyKsUW0KGF+qYCuUkSfosaqNCMmnrOXxAAAjOaAAIS00J9482y+xs14a1PLvcl4kDiQtfF5Pxbysp56UQAAifmAAIRUzvaE1Wxm"
            + "xVCC2lNpvBiBqDhLtACBXRdJsAiCCgAAiiWAAIRDzU6Qk2w4w6SNa1Mwuk+LKTg0siOKOhe0rcSJKQAAikmAAIQjy/ucm2w3wimY"
            + "OFM8uMSVFThVsIWUBBgqq+qQmgAAimWAAIPwyvKo8mxCwQCjNlNut4yfZDiWr1Cd6hieqniQpQAAinmAAIO+yhK1xWxYwA2uiFO1"
            + "toqqGDj6rkylIRkmqWORAQAAioiAAOsVeF91rMaPeWd2kqCbemp3mnj0e3V42k49fJ16LhhnfjR7K+fcdZaH3sPxduaF6J5YeC6E"
            + "ZXb0eXyDRExkesOBZhYLe6aCYOT1c4KaOMEudOyVl5vodl2RdHTod92N+EqueTaK5RP8eYWHxOKLcgWsn77Qc3CljJmxdO6e93L7"
            + "doaZE0kmd+eUNBJed9yL8eDCcQu/NL0Mcmm1yJf0c+Cs0XF/dYek00fqduGebBEkdqiLl9+BcHjSIbvDccDGXpaScyC7JnBEdMix"
            + "Zkbrdhyo3xA7dcqK+dFzhodze7FXhWV00480hIh2KGrUg+13pkLLg9J5LA1EhaF6ps9KhBWELK+IgyaC/I1mgmyCD2kXgfaBb0Es"
            + "gfKBAwvygy+Au8zmgiCU+q0LgUSRdosqgKuOPGc8gGiLjz+rgHCJewqggSaFqcrMgLWl5ar9f96gRIk7f1Ca4GWafxmWHj5qfyeS"
            + "zgm1f5OGkMlCf8K3JqlvfuivbYewflKn52RNfiihZD1ofiicigkEfmmGGMg0fyrJF6hCfki/QYZqfZe1oGM2fW+tyDyYfXGk3Qh4"
            + "fY+FuritlWpx5Jwikj1zYn1yj4h03Vw2jUp2fzahjC14CAPui1h7Mbb/k0iBMZqJkECAe3vjjaJ/8FrHi35/vjVZil1/xAOAiOSA"
            + "SrVAkXWQYpipjoSNqXoui/CLN1lNieyJRzQviMyIJgMWhuqCFrONkCCgA5cEjTObcHiBiq2XHVfkiKyTcjMlh4eRUgLBhV+B3bJJ"
            + "jz6v2ZW5jFOpe3c9icSjXlbRh8meZTJghoSZ6gKJhDuBtrFmjqfAbpS7i7y4SnYwiRywdVXshx6q0DG0hdmgLgJYg2iBlqEnpR9w"
            + "6IdooCdyXmuym7pz4U1Ol/N1gimHlo12sQBCiat+NKAAo05+OoZanl59ymqfmet+RExMlil+cii7lJZ+dAA8h9p/+Z6zoaOMw4Tm"
            + "nMKKi2lFmFSIoUsolKOHZCfykvWG4AAyhkqAIp2EoFSa4oOtm36XGWgNlw2Tpkoxk0iRACdYkXuPdgAuhSOAH5yKn3ypdoLDmqCk"
            + "IWcXliCfM0lYkmGb0CbSkGSWKwAohD6AG5vTnvK4qYIAmg2x2WZNlXyrpUivkbinqyZfj7CZ7QAkg5iAGIsUtdpwE3OXr1NxYVpf"
            + "qVVy0T5ZpEN0ThvcovB1FAAAhEyAAIp4tD19anMZrZZ8/1nZp2t8UT3koj58lxvLoG19BgAAg7uAAInAsoiJkXI1q+mH4lj/pbmG"
            + "kD04oI2FrhuWnmiFFwAAgyWAAIj3sUGWeXFqqqCRiVgypGWQjDyinx6O5RtunK2MUQAAgsKAAIhlsFijl3Deqa6fKleno2SbaTw0"
            + "nhCZahtcmzyQ0AAAgoyAAIfhr9CxlXCCqQSr4Vc8opCnLzvdnTGjxxtEmlGScAAAglqAAHYvx6VvUWCJv9ZwVUlduIRxki8lspxy"
            + "vA5ysAxz1wAAgUKAAHYlxgl7Z2CRvfx64ElftlN6jC9PsBt68g8QrQZ7lQAAgUGAAHYaxBmG4WBRvAyFE0kgtE6D2y9DrfmDfw+Y"
            + "qlqDDwAAgUCAAHXowpmSgGAsunCPu0j5sp2NfC9IrCKNCBARqB+IWAAAgT+AAHW0wZmef2AkuUGaxkj6sVCYAS9aqsWXEBB4pmKL"
            + "IgAAgT+AAHWKwOyrBmAsuF2mWEkMsEmjFi9/qa+euBDJpSmLWQAAgT6AANc6cH1rYrYwcrduCZNRdL9wkm5PdqhzEkXyeG51YxEp"
            + "egR2FdQlbRl9PbOub699E5EpcgZ9G2xudDp9UkRDdhF9dg9zd2R809FMamOPErDobSGMUY69b6+J2Gpvch+H10Ktc/yF7A4MdUGD"
            + "as7waHCg4a6Uaz6b2IyCbeGW/2iPcG6SoEFAckmOXAzuc5qILM1KZxuyuKzpaeWriorTbISkZGcYbyGd+UAecOiZAAwgcmCIM8xD"
            + "ZlXEs6vJaQu7Z4mGa4uyE2Xobiep8j8lcR2jAguGcZaHyr8zff1pzKJYfihsy4L0fnVvfGDgfuNyFDq1f6F0Vwb6gYd16L07evN6"
            + "ZaB5e3J67YEme/F7Ql9GfIh70jlXfT18aAY4fvB8obrneGiKk54HeQqI2H73ebSHMl13eoCFyjfseyyEkwV6fUuB37jldpSbBpwH"
            + "d0WXRX0Pd/+TelvjeNyQEDa9eXqNvgTwe+aDS7d6dU6roJqMdf+l53uWdrSgElqed5+a+DXOeB2XNgSLewuDEraXdIy8mpl8dS20"
            + "+XppdcatKlmUdq+mwzT3dxufWwQ6ekuC26gKjGZomI6dioFrp3JUiRduYlMFiA1w+C6yiDJy6AAAhgF3w6aYiaV3joz8iBV4cXC7"
            + "hsd5RFGlhdF6Ky2Thcd6bwAAg8t9c6TkhzqGW4sehdCFZm8YhJyEV1Bdg76Dhiyjg5WC+AAAgfKAAKMzhYGVnYmHhCGS3G12gveP"
            + "8078giWNbSuogd6L/AAAgFCAAKIDhFOk34hFgvmgbWw0gcKbwk3lgPOX8irygHWUcAAAgACAAKFCg5i0rYdRgjuum2spgOGoPE0B"
            + "gAqjtCpKf3iarwAAgACAAJG8m6xnwXrCmBlqrmEilRxtWkRYkrlvzSE6kwZxAwAAgJ59F5CymVV1dXmhlfF2cWAHkut3yUNokIB4"
            + "wCCjkFp40AAAgFN/oI9klxiDEHggk9GCkl6vkNqCA0JVjoKBvCABjgqA6wAAgACAAI4tlWyQwnbikjaOql14jz2Mh0F1jNKK3h+I"
            + "jBKJtQAAgACAAI0+lFCezHX+kRqbTFyBjgqXtECSi56VRh8Pio2R4wAAgACAAIyfk6GtO3VAkGKod1u2jTOjoD/jiriguB6biX+U"
            + "kgAAgACAAHyrrApmvmd+pydpb1ALoslr9TU5n3huGBOlntBvFAAAgACAAHv5qhh04GbmpRh1jE+EoIB1XjTinP92WBO7m8F3CQAA"
            + "gACAAHsqp+R//WXqovR/VU6nnl5/DDRDmtd+7hOfmSd/JgAAgACAAHpXpkSMtmUWoVmLO03RnLeJNDO0mRGIWxOHlvaGegAAgACA"
            + "AHnCpR6Y7mSCoCyWSU02m3eTwDM6l72SiBODlTCLOgAAgACAAHlQpHemNmQzn2OipUzJmnafLjLclqKcwxNik/WNGwAAgACAAGh/"
            + "vbVleFSOt/1nyT7OspVqBCU5ryBrhQfJqLluZwAAgACAAGgTvBFxOFR1tdxx+T7dr/1y7yWWrBRznghHpa52DQAAgACAAGfOub98"
            + "gFQOs4V8Iz6SrZF8ByWYqXN8FwiVowp9AwAAgACAAGeEt/OH7VPXsaCGuj5Tq5eFnSWXp0WFmwjSoOGGGAAAgACAAGdHtsGTxVPL"
            + "sDWRuj5BqgyP5CWWpZ2PfAkEny+GGQAAgACAAGcitgWfdlParyqc9z5CqNia5CWipFGXzwkcne2GKQAAgACAAMOGaCdhMqWPa2Jl"
            + "ZIWxbldpVmNrcRptFz11c05waQrFdpJxm8B4Y8Ryn6MlZ390IIOfat11pGGhbfd3KjvtcCN4bAnQc+N4xb2WYDiDtqBlZDyC1IFC"
            + "Z+OCBF+ya0WBUDp1bV6A3Aj8cjZ/tbssXXSVAJ4cYaeR3H8dZXeOv133aQCL3jkwaviI5Ahics+FD7mCW5amMZyBX9ig732JY7Kb"
            + "n1yYZ1CW4jgxaRuTbAfncvmFWLiEWmm3VptzXqGv7nxiYmWog1uMaQGiLDdUZ5uc+gePczmFHa1ddPJgMJM3dkpkmHZcd7BojFaO"
            + "eR5sOTIyeoBvLQJXfm1yVqtVcPBwHJFbcsJyI3SkdG1z+1Ucdgx1ujERdzV3AAIrfcN5bqjwbZaAAo7mb6l/6HKBcZB/u1Ncc21/"
            + "hy/CdGJ/SgHifV5/IKbkav6P7ozpbTaN3XCkbz+LnFHlcTOJfS64cemIRQG8fUaBFqV3aTqf54t2a36b7m87bY2Xt1Cvb5CUEC3n"
            + "cAqRewGZfU2BFKSZaBiwAopyalKqKm4jbyqkEE+6bkufKi0dbpeZhQF7fX+BAJe5gptfa4CbghpjyGaDgfpnq0kZgiRrMCXgg0ht"
            + "agAAgA93IJYlfuht/H7pft5wR2Tufu9yVUfTfzF0MiT+f+B1KAAAgAB9AZRZe7981nz8e/d9K2NYfDF9dEadfJN9qyQ9fPF9ogAA"
            + "gACAAJKgeVSLMHtgeaqKGWG2efiIhEVQemSGyyNYeoeGMAAAgACAAJFrd6+Z0nokeBCW+mCEeFuTz0RHeMiRQCK8eK6OwwAAgACA"
            + "AJCieF2ounk5eIGkWl+QeJSfm0N1eMycWCIVd1OUtwAAgACAAILGkWNeom3Rj01iwVYMjcRmezq4jN1psBhzjchrHAAAgAB8SYGE"
            + "jh9r62ySjFxuQFTwitpx0znmieVzihgkijty7wAAgAB/hIAWiyh5JWsGiZl6wlOliDB7Ujjzh0Z7DBe9hzp7SwAAgACAAH6+iNeH"
            + "MmnFh2KGllJ/hfiFlzhMhPKFEReChKGEuQAAgACAAH2+h1OT6GjiheSR+1GDhGKP0Ddag1KOKxcbgq6MwwAAgACAAH0Ohk+homgd"
            + "hNyekVC7gzia/jatggyZKha/gT6PMAAAgACAAG6KoWddcVtAniRhPUVim3NktCuGmhhnVwvumAVpWgAAgAB/rm2FnqRppFpwm2dr"
            + "40TDmIFt/Cs+lt1viQwZlGlxQwAAgACAAGyEm8h1s1lNmKx2pEPTlc93dyqilBh4BQwTkUN5WgAAgACAAGuGmY+B3VhcloKBrULr"
            + "k5qBSSoYkbSBDwwAjp2A1wAAgACAAGrRmAuPGlewlQCNxUI+kgOMACmMj/2LNAwBjIyF2AAAgACAAGpKlxKax1dTk/OY0EHWkLqW"
            + "dik9joSVCgvpiwiIDgAAgACAAFrZswpbn0iSryNe7zQRq79h8hp6q4djZwKTngRpwwAAgACAAFnysLxm6kgWrHpo6TPfqKNqyhrQ"
            + "p7prhgMNmnhxeQAAgACAAFlord5x7Ud3qZ5y3TNxpbRzuRrNpHJz4QNbl1R3ZQAAgACAAFjhq4h9E0cQpz99LjMQozp9HRrMoZp9"
            + "AgOWlKZ+vAAAgACAAFh/qfqIZEbmpYmH0jLpoWCHExq+n4iHOwPHkpuCPQAAgACAAFhCqO+T60bdpEOS7DLgn+2RzBrOnd2PhQPe"
            + "kQeCnQAAgACAALCcXsNXSJVzYulc2XhjZs1iIli2amhnHDUBbJ9rZgWYdott+614WMdoLJMAXa1rPHZOYiNuMFb0ZipxATObaCRz"
            + "WAU+dyR1e6qBU9p45pBFWUJ5m3QDXiV6T1UbYoF7DDJIZE17pATrd7B8uqf0T++JfY4IVbqIMXIBWueG3FOQX3iFXjEyYReEegSr"
            + "eBuCaaYdTPSZ9Yx3UwSWY3ChWGKS31JiXRKP0TBnXpSN3gSAeGWDC6UBSxuqSYtvUT2khG+oVp6fAFGNW1aafS+1XLSXBgRaeKWC"
            + "8ZxqasBW3IRrbUxccWnfb81hlEw/ci5mTSlxc95p0ACFfyBwwpoGZTRmQ4J4aHBpqGgza1xsyUrrbf1vpiiIb3txmwB5fzR3+Jd7"
            + "YIh1w3/6ZDR3FmYbZ3x4N0k+amF5Pidea7Z5zwBlf1V955VVXNiFdH3+YNCFLWROZFaD6kflZ1iDByZ7aKCCqABaf2iAFZPOWg2U"
            + "ZnyRXjOR9WL9YduPS0bHZOqNEiXVZiiLnABSf3aAN5LdWEqjt3uSXHufcGH/YBya90XxYyiXkCUmZF6TlwBKf4SAMohTd6RWd3L5"
            + "eJhb4FrIecVgzz8Yew5lLxzkfO9nsQAAgAB2mYZWcnVkhnEndAloDFk3dYRrPj3oduhuChw/eINvbwAAgAB8rYRTbg1ymW8icAh0"
            + "WVemccR12DzDc0V3FxuxdL53owAAgACAAIJ3apWBC213bM2CI1YEbrOAnjuGcDeAjxr0ccqBjAAAgACAAIEkZ/KOsWwzak+Ms1TW"
            + "bEaLfjqHbbuKNBqAb4KJUwAAgACAAIBHZj6dC2tJaKeaHFPuapKWxDnEa/GUrRntbfSOtAAAgACAAHRmhbxVzmEJhU1a6ErhhUdf"
            + "jzDFhdNjZA/fhx9lcAAAgAB7pXK3gQJimF+TgRFmGEmugSxpQS/3gZlryQ+xgsJtNwAAgAB/T3EOfOBvd13cfUBxc0hJfYdzKC7z"
            + "fet0aw9Wf2p1fgAAgACAAG+KeZV8WFxyeip88UcJeoB9OC5Cerl9Yw8qfJ99zwAAgACAAG5fdxeKylt1d8+KCkYTeByJAC1eeCaH"
            + "9Q7eeoyHZgAAgACAAG2VeVmWbFqqeXeUnkVUdnOSVyy+dk6RgQ6JeOGJVwAAgACAAGDdlXFUUk8Yk9pZCDqFkvBdRCDqlAVgBQXf"
            + "jkhk8gAAgAB/TF9tkTdgAU3qj+pjYTmajuhmXSCDj3ZoHQYHipBsxwAAgACAAF4kjUZrwkyPjDJt2DiIiz9vnB/ai39wbwX+hzx0"
            + "NAAAgACAAFztih56VUtxiTN4kjeFiDl5Jx9biA57bgX2hHV7dgAAgACAAFv2h6yDb0qNhueDVDa6hd6C6h7KhUiC0QYLgpWBiAAA"
            + "gACAAFtHhhmPvUoehWmO2jZshB2NYB7AgxiMqgYIgUKEFAAAgACAAE1UpzRR7jzhpOVWHSlmo7JZqw+7pfVbHgAAj8Bm6AAAgACA"
            + "AEwAo4RcszvDoVNfxyiLn85iVA+zoOpjIwAAjTpuXgAAgACAAEsmn55nazrGnYxpcSfKm/lrBA+LnFBrUwAAiu1xTAAAgACAAEpN"
            + "nGdyVjoKmndzdSc0mLx0Gg+FmFV0NAAhiNx4igAAgACAAEmVmfJ9bTmEmB99yCbTljx9sg9flUB98AA/h0d/kgAAgACAAEkqmE2I"
            + "2Dk6ln2IqCatlGuIHg9ykwGGrwBQhiGANgAAgACAAJ77U9VN1YZSWORUomvRXcJbLE6HYi9hUSzQZBFmfAF7fYBrGZujS5xeKIO6"
            + "UcFiq2mjV3Fm/Ey6XF5rCSt/Xm1uXQGUfVVy2piIRG5uaID7S3twqWdoUdJy1kr8VyF06CpZWYR2vAGofTJ6V5W7Pl9/J37HRkR/"
            + "JWWXTS1+30mzUsJ+9ymAVYF/AgG4fReAOZObOZCNw303QiqL9mRtSZOKjki8T1GJISjvUluIkwHEfQSBMpIzNg+dtHwpPyKZhmOy"
            + "RuGV6UgzTLqTMCiCT/KRXQHNfPWBOIysXw1OAHZSYsJUiF3ZZmFaxUJEaZRgfiDcbBZkgwAAgABwJommV0VcznQrXANha1wnYEll"
            + "wUD2Y8JprSAaZntsRQAAgAB3Z4bZUGNr1nGXVepudVoSWsFw5D9VXnlzEh8XYbR0bAAAgAB9cISDSqt6uG+WUM97dlhXVhp8DT4j"
            + "We18kR5rXmd9LwAAgAB/yoLRRi+JgW4oTMyIb1chUmyHNj0kVjiGVR34XAqF/AAAgACAAIG0QueX420rSdeVEVZIT6SSKjx5U12Q"
            + "OB16Wq2N2QAAgACAAHo7aw1OCWXvbYBUM09eb/haETVCciJfOhRZdXJiPwAAgAB2MHdxY7dbfWPgZwlgCk2/aftkRzQNbCxn6BPU"
            + "cGhp9QAAgAB8WHUJXSxpHWGvYSlr90wnZIZuljLxZq5wwBNxbPNyFwAAgACAAHLzV7R231/qXC94F0qBX9p5EDHDYdl53xLhaoh6"
            + "5AAAgACAAHF4U2WEk16bWDuEOklXXBaDjTDPXcWDIxKUaKqC4wAAgACAAHB8UC+R1l2vVUiQL0h7WTCOIjAcWouNIxIgZ3yI+AAA"
            + "gACAAGdGeFRNdVToeaFTP0ALexxYrCbSfNFc8wjpf0ZgyAAAgAB7ImTVcYdZp1MZc35eGD6ndTBiJCXndn5lLAjBe6FoewAAgAB/"
            + "GGLCa1BmHlEibctpFz0db7xrviTNcJ5tlAh3eLRwrQAAgACAAGDyZgxynE+CaOx0Lzu/awB1dCQSa1J2SwhbdmV4pgAAgACAAF+S"
            + "Yc6CQ05hZQyBjTq8Zy6BtiMqZx9/kAgodK9/3AAAgACAAF6hXpOLsU2BYhCK6DnxZDCJriKJY/GJkwflc4WEcwAAgACAAFQlh3dL"
            + "w0Obh7JRMDANiJRWBBaEizlYygGZhRxhwQAAgAB+3FINgS9W5UHggfRbLC6mgsle2RXShE1guAGjgdppfAAAgACAAFBVezxiR0Ao"
            + "fGFlSy1HfUBnzBT4fhlo0gGaf09umAAAgACAAE7FdhFydz60d5J07iwEeGp2JRRTeQVxagGTfdV1uQAAgACAAE2Dcc55ZD2Mc7N6"
            + "HisNdH56eRO3dT56uwGkfTl+rAAAgACAAEyebn2FQzzYcMSFFCqZcXaEexOCclaESQGOfWCBDQAAgACAAECZmUVIqzGpmJ5NqB6g"
            + "meRRcwZamjxT9wAAgtFmJQAAgACAAD6jk4xS4y+Xk4tW1xy2lKBZqAW3k0dbywAAgGdtxwAAgACAAD1Ejc5dUC4Djh1gLBthjwJi"
            + "BwVOjR1j3wAAgAB1PAAAgACAADv0iJJoAyy/iVBp1Bpgie5qzQUtiB5smAAAgAB7UAAAgACAADrYhDBy6Su/hW5zyxmuhdp0DgT6"
            + "hGV2HgAAgAB/IgAAgACAADoNgKp+Jyr9gmN+PRk5grZ+CgTxgbJ+1QAAgACAAAAAgACAAAAA//8AAP//AAD//w==";
    }
}
