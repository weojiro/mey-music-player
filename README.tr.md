<div align="center">

# mey music player

**Dinlediğin şey, Windows görev çubuğunun tam üstünde.**

<img src="docs/open-close.gif" alt="Görev çubuğundaki kapak oynatıcıya açılıyor" width="452">

[**İndir**](../../releases/latest) · [Web sitesi](https://weojiro.github.io/mey-music-player/) · [English](README.md)

</div>

mey music player (MMP), Windows görev çubuğu için ücretsiz ve açık kaynak bir müzik oynatıcı. O an çalan
şeyin kapağını görev çubuğundaki ikonlarının yanına koyar: Spotify, tarayıcıdaki YouTube, SoundCloud ya da
Windows medya kontrollerini kullanan herhangi bir uygulama. Kapağa tıklayınca küçük bir oynatıcı açılır;
butonlar, süre ve Spotify'da şarkı sözleri oradadır. Renklerini kapaktan alır, böylece her zaman müziğe uyar.

## Tutorial

[<img src="docs/tutorial.png" alt="Tutorial" width="640">](https://www.youtube.com/watch?v=PsI_AHPoIt4)

## Özellikler

<table>
<tr>
<td width="50%"><img src="docs/player.png" alt="Butonlarıyla oynatıcı" width="100%"><br>
<b>Görev çubuğunda bir oynatıcı.</b> Önceki, çal / duraklat, sonraki, ilerleme çubuğu, çalan uygulamanın
sesi, karıştır ve tekrarla. İstediğin yere koymak için kapağı görev çubuğunda sürükle.</td>
<td width="50%"><img src="docs/chameleon.png" alt="Dört farklı kapağın renklerinde oynatıcı" width="100%"><br>
<b>Kapaktan gelen renkler.</b> Her şarkı oynatıcıyı kendi renklerine boyar.</td>
</tr>
<tr>
<td><img src="docs/lyrics.gif" alt="Oynatıcıda satır satır değişen şarkı sözleri" width="100%"><br>
<b>Spotify'daki gibi şarkı sözleri.</b> Açık oynatıcı o an söylenen satırı gösterir; tray menüsündeki
<i>Şarkı sözleri</i>nden kapatabilirsin. Butonlar için fareyi üstüne getir.</td>
<td><img src="docs/title-card.png" alt="Kapağın üstünde şarkının adı" width="200"><br>
<b>Şarkının adı bir bakışta.</b> Fareyi bir saniye kapağın üstünde tut.</td>
</tr>
</table>

### Oynatıcını tasarla

<img src="docs/design-demo.gif" alt="Yeni bir oynatıcıya ad, renkler, dönen bir plak GIF'i ve bir şimşek PNG'si ekleniyor; resimler taşınıyor, büyütülüyor ve döndürülüyor" width="900">

Oynatıcıyı kendine göre yapmak için tray menüsünden **Ayarlar**'ı aç:

- arka plan ve buton renklerini seç ya da kapaktan gelen renkler için *Bukalemun*'da bırak;
- *Saydamlık* kaydırıcısıyla oynatıcıyı saydam yap (en fazla %90): hepsini birlikte ya da arka planı, butonları ve
  kapağı ayrı ayrı;
- PNG, JPG veya GIF resimler ekle, büyük önizlemede sürükle, boyutlandır ve döndür;
- hangisinin önde duracağını resim listesinde yukarı aşağı sürükleyerek seç. Resimler hep butonların arkasında kalır;
- 5 oynatıcıya kadar kaydet ve aralarında geçiş yap. Her şey kendiliğinden kaydedilir.

### Ekranın her yerinde, oyunların üstünde bile

Yine **Ayarlar**'da, *Yerleşim* altında:

- **Serbest hareket**: kapağı ekranda istediğin yere sürükle. Bir oyun çözünürlüğü değiştirse de ekrandaki
  yerini korur. Açıldığında oynatıcı hep ekranın içinde kalır;
- **Oyunların üstünde göster**: oynatıcı tam ekran uygulamaların ve oyunların üstünde görünür. Bir oyunda görünmüyorsa
  oyunun ayarlarından çerçevesiz ya da pencereli tam ekranı seç;
- **Göster / gizle**: oynatıcıyı gizlemek ya da göstermek için **Ctrl + Alt**'a bas;
- **Fareyi oyundan al**: tam ekran oyunda **Ctrl + Alt + X**'e basınca fare oyundan çıkar; şarkı geçebilir ya da
  başka bir şey kullanabilirsin. Oyuna dönmek için tekrar bas ya da oyuna tıkla. *Oyunların üstünde göster* açıkken çalışır.

Kısayolları değiştirmek için Ayarlar'da üstlerine tıkla. Oyun imleci gizlerken (araba sürerken, nişan alırken)
tıklamalar oynatıcıya değil doğrudan oyuna gider. İmleç geri gelince (oyunun menüsünde) oynatıcıyı her zamanki
gibi kullanıp taşıyabilirsin.

Kaydettiğin her oynatıcı kendi yerini hatırlar: taraf, kapağın yeri, *Serbest hareket* ve *Oyunların üstünde göster*.
Başka bir oynatıcıya geçince tam bıraktığın haliyle geri gelir.

### Hızlı kontroller

| Nerede | Ne yapar |
|---|---|
| Kapakta fare tekerleği | sonraki / önceki şarkı |
| Kapağa orta tık | çal / duraklat |
| İlerleme çubuğunda tekerlek | 5 saniye ileri ya da geri |
| Ses ikonunda tekerlek | ses; tıklayınca sessiz |
| Tray ikonu | tık çalar / duraklatır, tekerlek sesi değiştirir |
| Ctrl + Alt | oynatıcıyı gizler / gösterir |
| Ctrl + Alt + X | fareyi tam ekran oyundan alır (*Oyunların üstünde göster* açıkken) |

Tray ikonuna sağ tıklayınca: Ayarlar, Oynatıcıyı gizle, Şarkı sözleri, Windows ile başlat, görev çubuğunun
tarafı, dil, Yeniden başlat ve Kapat.

## Kurulum

1. [Son sürümden](../../releases/latest) **MMP.zip**'i indir.
2. Klasöre çıkar ve **MMP Launcher.bat**'a çift tıkla. Eksikse .NET Desktop Runtime 8'i de kurar.
3. "Installation complete!" yazınca uygulamayı masaüstündeki **mey music player** kısayoluyla aç.

MMP bundan sonra Windows ile birlikte açılır (tray menüsünden kapatabilirsin) ve kendini günceller: açılışta
ve günde bir kez yeni sürüme bakar. Windows 10 (1809 veya üstü, 64 bit) ve Windows 11'de çalışır.

## Sorular

<details>
<summary><b>Windows "Bilgisayarınız korundu" / bilinmeyen yayıncı diyor.</b></summary>

Uygulama ücretli bir sertifikayla imzalanmadı. *Ek bilgi*'ye, sonra *Yine de çalıştır*'a tıkla.
</details>

<details>
<summary><b>Hangi uygulamalarla çalışıyor?</b></summary>

Windows medya kontrollerinde görünen her şeyle (ses / hızlı ayarlar menüsündeki medya kutusu): Spotify,
YouTube ya da SoundCloud çalan tarayıcılar ve çoğu müzik ve video oynatıcı. Şarkı sözleri sadece Spotify'da.
</details>

<details>
<summary><b>Ses ayarı Spotify'ın içindeki ses çubuğunu oynatmıyor.</b></summary>

MMP uygulamanın sesini Windows ses karıştırıcısından değiştirir, kulağa aynı gelir. Tarayıcıda bütün
sekmeler tek bir sesi paylaşır. Firefox'ta ses MMP'den değiştirilemez (Firefox Windows'a hangi uygulama
olduğunu söylemiyor), geri kalan her şey çalışır.
</details>

<details>
<summary><b>İnternete ne gönderiyor?</b></summary>

Yeni sürüm için GitHub'a bakar. Şarkı sözleri açıksa, sözleri bulmak için Spotify şarkısının adı, sanatçısı,
albümü ve süresi [LRCLIB](https://lrclib.net)'e gider; bulunan sözler `%LOCALAPPDATA%\MMP\lyrics` içinde
saklanır. Başka bir şey gönderilmez.
</details>

<details>
<summary><b>Görev çubuğum yanda ya da başka bir ekranda.</b></summary>

Görev çubuğunda MMP ana ekranda, altta ya da üstte durur. Ayarlar'dan *Serbest hareket*'i açarsan istediğin
yere, istediğin ekrana koyabilirsin.
</details>

<details>
<summary><b>".NET eksik" diyor.</b></summary>

**MMP Launcher.bat**'ı tekrar çalıştır.
</details>

<details>
<summary><b>Bir şey çalışmıyor.</b></summary>

MMP beklenmedik hataları `%LOCALAPPDATA%\MMP\error.log` dosyasına yazar. Dosya bilgisayarında kalır;
[bir issue açarken](../../issues) ekleyebilirsin.
</details>

<details>
<summary><b>Nasıl kaldırırım?</b></summary>

Tray menüsünde *Windows ile başlat*'ı kapat ve *Kapat*'ı seç, sonra `%LOCALAPPDATA%\MMP` klasörünü
(uygulama, ayarları ve kaydettiğin oynatıcılar) ve masaüstündeki kısayolu sil.
</details>

## Diller

English, Türkçe, Español, Français, Deutsch, Português (Brasil), Русский, 简体中文, 日本語, العربية, हिन्दी.
Tray menüsünde *Dil*'den seç.

## Geliştiriciler için

Kodun yapısı ve build alma [İngilizce README](README.md#for-developers)'de.

## Lisans

[MIT](LICENSE) © weojiro
