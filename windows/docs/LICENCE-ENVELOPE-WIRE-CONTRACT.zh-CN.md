# 许可证 envelope 的跨端 wire 契约：C# 端读不出 Go 端发的文件

> 状态：**待修**。服务端已发版，客户端读不了。修在 C# 端，本仓库的 Go 侧**不需要动**。
> 适用分支：本文件在 `feat/server-convergence` 上首次提交，内容对四个 feature 分支的
> `windows/` 目录同样成立（该目录在各分支内容一致）。
>
> **本次审计的验证边界**：Go 侧结论由在本机跑通的代码得出；**C# 侧结论全部来自源码阅读，
> 未经编译、未经运行**——审计时所在机器没有 dotnet、没有 Windows。凡是本文说 C#「会炸」的地方，
> 都还没有被任何一次 `dotnet build` 证实或证伪。落刀时请以本地编译与测试为准。

---

## 1. 结论

服务端签发的 `license.json` 里，`iat` / `nbf` / `exp` 是 **JSON 数字（Unix 秒）**；
客户端 `LicenseEnvelope` 把这三个字段声明成 `DateTimeOffset`，且**全链路没有任何自定义 converter**。
`System.Text.Json` 的 `DateTimeOffset` 默认转换器只接受 ISO-8601 字符串，喂数字直接抛 `JsonException`。

**canonical payload 本身逐字节一致，签名算法也一致——但 envelope 在反序列化那一步就断了，
根本走不到验签。** 这是最需要警惕的一类缺陷：根因在序列化层，报错指向别处。

## 2. 症状：它不会报错，它会说「你没有许可证」

完整链条：

```
服务端写出  {"iat":1767225600,"nbf":null,"exp":null,...}
   ↓
LicenseStore.Load()
   ↓
JsonSerializer.Deserialize<LicenseEnvelope>(...)  →  JsonException
   ↓
catch (JsonException) { return null; }            ← 吞掉
   ↓
LicenseStore.Load() 返回 null
   ↓
用户看到：未激活 / 没有许可证
```

`LicenseStore.cs:54` 的注释写着「A truncated or hand-edited licence is not a licence.」
——在当前这条链上，这句注释**给出了错误的诊断**：文件没有损坏，也没有被手改，
它只是一份格式正确、但本端类型声明读不了的文件。而这个 catch 把类型不匹配伪装成了
「文件坏了」，于是排查方向会一路走到磁盘、权限、加密、备份，而永远走不到序列化配置。

`RedeemService.Accept()` 走的是另一条路（`_json`，同样零 converter），失败时表现为
`MalformedResponse`，同样看不出根因。

## 3. 证据

**服务端（Go）——实发形态，以共享测试向量为准：**

```json
{
  "v": 1,
  "sub": "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
  "plan": "buyout",
  "iat": 1767225600,
  "nbf": null,
  "exp": null,
  "ent": ["pro"],
  "sig": "..."
}
```

来源：`shared/License/license-test-vectors.json` 的 `vectors[].envelopeJSON`，
该字段是服务端 `license.Envelope.JSON()` 的原样输出，不是手写的样例。

**客户端（C#）——本机无法执行，以下为源码事实：**

| 位置 | 内容 |
| --- | --- |
| `LicenseEnvelope.cs:27-28` | `[JsonPropertyName("iat")] public DateTimeOffset IssuedAt { get; init; }` |
| `LicenseEnvelope.cs:31-32` | `[JsonPropertyName("nbf")] public DateTimeOffset? NotBefore { get; init; }` |
| `LicenseEnvelope.cs:35-36` | `[JsonPropertyName("exp")] public DateTimeOffset? ExpiresAt { get; init; }` |
| `LicenseEnvelope.cs` | 全文 `JsonConverter` 出现次数 = **0** |
| `LicenseStore.cs:88` | `JsonOptions` 只设了 `WriteIndented` 与 `Encoder`，**无 `Converters`** |
| `RedeemService.cs:42` | `_json` 同样无 converter |

## 4. 修法：改 C#，不要改 Go

推荐给三个属性各挂一个 converter，落在 `LicenseEnvelope.cs` 或同目录的新文件里：

```csharp
internal sealed class UnixSecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        => DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64()).ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions o)
        => writer.WriteNumberValue(value.ToUnixTimeSeconds());
}

internal sealed class NullableUnixSecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        => reader.TokenType == JsonTokenType.Null
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64()).ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions o)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value.ToUnixTimeSeconds());
    }
}
```

然后：

```csharp
[JsonPropertyName("iat")]
[JsonConverter(typeof(UnixSecondsDateTimeOffsetConverter))]
public DateTimeOffset IssuedAt { get; init; }
```

`nbf` / `exp` 用可空版本。`System.Text.Json` 随共享框架提供，**Core 工程不需要新增任何
`PackageReference`**，`net10.0` 目标框架不变。

**为什么不改 Go 侧**：让 `Envelope` 改成 `time.Time` 或字符串，会让
`Canonical()` 里的 `strconv.FormatInt(e.IssuedAt, 10)` 失去秒语义，并且与已经产出、
已经入库的 `shared/License/license-test-vectors.json` 正面冲突——那个文件是跨端共同持有的
契约，改它等于同时推翻已定的规范格式。Unix 秒入规范串本来就是设计文档 §10.2 的原意，
所以是 C# 端需要向 wire 格式靠拢。

## 5. 怎么验证

`shared/License/license-test-vectors.json` 就是现成的回归夹具——**服务端已经产出，
C# 测试工程直接读它**，两边从此对同一份字节说话：

```csharp
// 建议加在 LicensingCanonicalPayloadTests.cs 旁边
[Fact]
public void EverySharedEnvelopeVectorDeserialises()
{
    var doc = JsonDocument.Parse(File.ReadAllText(SharedVectorPath));
    foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
    {
        var envelope = JsonSerializer.Deserialize<LicenseEnvelope>(
            v.GetProperty("envelopeJSON").GetRawText(), JsonOptions);

        Assert.NotNull(envelope);
        // v.claims 就是 envelope 的内容，两侧必须一致
        Assert.Equal(v.GetProperty("claims").GetProperty("sub").GetString(),
                     envelope!.Subject);
    }
}
```

这一步同时补上两个洞：一是本文的时间戳问题，二是**服务端与客户端的序列化行为从此有了
共同基准**。目前的 `LicensingCanonicalPayloadTests.cs` 只钉住了 canonical 字符串，
没有钉住 envelope 的解析——也就是说，签名算对了但文件读不出来这种事，现有测试一条都拦不住。

## 6. 同一轮审计发现的其它项

都不影响当前功能，但都是**静默失效型**的埋雷。逐条列在这里，避免将来当成新问题重新发现。

| # | 现状 | 触发条件 | 建议 |
| --- | --- | --- | --- |
| ③ | Go 用 **base64 std**，C# `Base64Url.TryDecode` 按 base64url 解。当前靠「`-`→`+`、`_`→`/`」的宽松映射兜住，实测含 `+`/`/` 的签名与 SPKI 都能 roundtrip | 任何人给 C# 加**严格字母表校验**，或让 C# 反向编码一次 | 上线前统一为 std（测试向量与设计文档 §10.14 的 `openssl base64 -A` 都是 std），并把两侧注释里「对方那套」的说法改掉 |
| ④ | Go `sort.Strings` 是 UTF-8 字节序，C# `StringComparer.Ordinal` 是 UTF-16 码元序 | 新增**非 BMP** 权益位（增补平面字符），两侧顺序会反转 | 当前权益位全 ASCII，不触发。要么锁死权益位只允许 ASCII，要么两侧都显式按码点排序 |
| ⑤ | `LicensePublicKeys.PlaceholderKeyV1` 仍是占位串 | 任何时候 | 有意为之（未发布构建不能自解锁）。**但今天客户端验不了任何东西**，上线前必须换成真公钥，且要确认旧公钥仍可验（设计文档 §10.3 的「新旧两把并存」扩展位） |
| ⑥ | Go 过期判定是 `now-skew >= exp`；C# `LicenseVerifier.CheckWindow` 是 `now > expires`，**没有给过期留容差** | 时钟偏慢的用户 | 设计文档 §10.13 承诺的「30 天回拨容忍窗」在 C# 侧没兑现。影响真实用户，不影响签名 |
| ⑦ | `ent` 全空时 Go 拒发、C# 接受 | 收到一份无权益位的许可证 | 策略差异，Go 更严。若要一致，建议 C# 对齐 Go |

**已核对一致、不用动的**：域分隔符、8 行行序、`\n` 分隔、无尾随换行、`-` 哨兵、
Unix 秒精度、时区归一、权益位的 trim/大写/去重、SHA-256 摘要、
**IeeeP1363 定长 64 字节**（不是 DER）、公钥 SPKI DER 编码。
`SharedCanonicalVector` 常量两侧逐字节相同，其 SHA-256 已独立复算比对通过。

## 7. 一条被证伪的告警（记下来免得再查一遍）

审计过程中曾报 `crypto.go:82` 的 `ecdsa.Sign(nil, key, digest)` 会因 `nil` rand 而 panic，
并附有 panic 栈。**该结论不成立**：在 Go 1.26.2 上以隔离复现与真实包调用两种方式验证，
`Signer.Issue` 正常签出 88 字符 base64（64 字节 IeeeP1363），`Verify` 通过，无 panic。
不要去改那一行。

顺带记一件相关的事：**许可证签发路径目前零测试覆盖**——全仓库的 `.Issue(` 测试命中
全部属于 `token.Issue`（另一个类型），没有一处覆盖 `license.Signer.Issue`。
本文第 5 节的测试建议顺带覆盖到它。
