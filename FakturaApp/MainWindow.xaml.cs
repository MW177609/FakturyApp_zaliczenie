using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure; // Wersja finałowa aplikacji

namespace FakturyApp
{
    // ==========================================
    // KLASY DO ODCZYTU DANYCH Z BANKU (NBP)
    // ==========================================
    public class NbpResponse
    {
        public List<NbpRate> Rates { get; set; }
    }
    public class NbpRate
    {
        public decimal Mid { get; set; }
    }

    // ==========================================
    // MODELE DANYCH 
    // ==========================================
    public class Kontrahent
    {
        public int Id { get; set; }
        public string Nazwa { get; set; }
        public string NIP { get; set; }
        public string Kraj { get; set; } = "Polska";
        public string Adres { get; set; }
        public override string ToString() => Nazwa;
    }

    public class PozycjaFaktury
    {
        public int Id { get; set; }
        public string NazwaTowaruUslugi { get; set; }
        public int Ilosc { get; set; }
        public decimal CenaNetto { get; set; }
        public int StawkaVat { get; set; }

        public decimal WartoscNetto => CenaNetto * Ilosc;
        public decimal WartoscBrutto => WartoscNetto * (1 + (decimal)StawkaVat / 100);
    }

    public class Faktura
    {
        public int Id { get; set; }
        public string NumerFaktury { get; set; }
        public DateTime DataWystawienia { get; set; }
        public int KontrahentId { get; set; }
        public Kontrahent Nabywca { get; set; }

        public string Waluta { get; set; } = "PLN";
        public decimal KursWaluty { get; set; } = 1.0m;

        public bool IsKorekta { get; set; } = false;
        public string NumerFakturyKorygowanej { get; set; }
        public string PowodKorekty { get; set; } // NOWE POLE

        public List<PozycjaFaktury> Pozycje { get; set; } = new List<PozycjaFaktury>();
        public decimal SumaCalkowita => Pozycje.Sum(p => p.WartoscBrutto);

        public string SumaDoWyswietlenia => $"{SumaCalkowita:N2} {Waluta}";
        public string TypDokumentu => IsKorekta ? "Korekta" : "Faktura";
    }

    // ==========================================
    // LOGIKA GŁÓWNEGO OKNA
    // ==========================================
    public partial class MainWindow : Window
    {
        public ObservableCollection<Kontrahent> ListaKontrahentow { get; set; }
        public ObservableCollection<Faktura> ListaFaktur { get; set; }
        public ObservableCollection<PozycjaFaktury> TymczasowePozycje { get; set; }

        private Faktura _edytowanaFaktura = null;

        public MainWindow()
        {
            InitializeComponent();
            QuestPDF.Settings.License = LicenseType.Community;

            ListaKontrahentow = new ObservableCollection<Kontrahent>();
            ListaFaktur = new ObservableCollection<Faktura>();
            TymczasowePozycje = new ObservableCollection<PozycjaFaktury>();

            dgKontrahenci.ItemsSource = ListaKontrahentow;
            dgFaktury.ItemsSource = ListaFaktur;
            cmbKontrahenci.ItemsSource = ListaKontrahentow;
            dgPozycje.ItemsSource = TymczasowePozycje;
            dpDataWystawienia.SelectedDate = DateTime.Now;
        }

        // --- ZAKŁADKA 1: KONTRAHENCI ---
        private void BtnDodajKontrahenta_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtNazwa.Text) || string.IsNullOrWhiteSpace(txtNIP.Text))
            {
                MessageBox.Show("Podaj przynajmniej nazwę i NIP!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int noweId = ListaKontrahentow.Count > 0 ? ListaKontrahentow.Max(k => k.Id) + 1 : 1;

            ListaKontrahentow.Add(new Kontrahent
            {
                Id = noweId,
                Nazwa = txtNazwa.Text,
                NIP = txtNIP.Text,
                Kraj = txtKraj.Text,
                Adres = txtAdres.Text
            });

            txtNazwa.Clear(); txtNIP.Clear(); txtAdres.Clear();
            txtKraj.Text = "Polska";
            MessageBox.Show("Dodano kontrahenta!", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnUsunKontrahenta_Click(object sender, RoutedEventArgs e)
        {
            if (dgKontrahenci.SelectedItem is Kontrahent wybrany)
            {
                bool maWystawioneFaktury = ListaFaktur.Any(f => f.KontrahentId == wybrany.Id);

                if (maWystawioneFaktury)
                {
                    MessageBox.Show("Nie możesz usunąć tego kontrahenta, ponieważ posiada on w systemie wystawione faktury!\n\nAby go usunąć, musisz najpierw usunąć wszystkie przypisane do niego dokumenty.", "Blokada usunięcia", MessageBoxButton.OK, MessageBoxImage.Stop);
                    return;
                }

                ListaKontrahentow.Remove(wybrany);
            }
            else
            {
                MessageBox.Show("Najpierw zaznacz kontrahenta w tabeli!", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // --- ZAKŁADKA 2: FAKTURY (ZARZĄDZANIE) ---
        private void BtnWystawFakture_Click(object sender, RoutedEventArgs e)
        {
            _edytowanaFaktura = null;
            txtNumerFaktury.Text = "FV ";
            cmbKontrahenci.SelectedItem = null;
            cmbWaluta.SelectedIndex = 0;

            chkKorekta.IsChecked = false;
            txtKorygowanaFaktura.Clear();
            txtPowodKorekty.Clear(); // Czyszczenie powodu

            TymczasowePozycje.Clear();
            AktualizujSume();

            MainTabControl.SelectedIndex = 2;
        }

        private void BtnEdytujFakture_Click(object sender, RoutedEventArgs e)
        {
            if (dgFaktury.SelectedItem is Faktura wybrana)
            {
                _edytowanaFaktura = wybrana;

                txtNumerFaktury.Text = wybrana.NumerFaktury;
                dpDataWystawienia.SelectedDate = wybrana.DataWystawienia;
                cmbKontrahenci.SelectedItem = wybrana.Nabywca;

                chkKorekta.IsChecked = wybrana.IsKorekta;
                txtKorygowanaFaktura.Text = wybrana.NumerFakturyKorygowanej;
                txtPowodKorekty.Text = wybrana.PowodKorekty; // Wczytanie powodu

                foreach (ComboBoxItem item in cmbWaluta.Items)
                {
                    if (item.Content.ToString() == wybrana.Waluta)
                    {
                        cmbWaluta.SelectedItem = item;
                        break;
                    }
                }

                txtKursWaluty.Text = wybrana.KursWaluty.ToString("0.0000");

                TymczasowePozycje.Clear();
                foreach (var pozycja in wybrana.Pozycje)
                {
                    TymczasowePozycje.Add(pozycja);
                }

                AktualizujSume();
                MainTabControl.SelectedIndex = 2;
            }
            else
            {
                MessageBox.Show("Najpierw zaznacz fakturę w tabeli do edycji!", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnUsunFakture_Click(object sender, RoutedEventArgs e)
        {
            if (dgFaktury.SelectedItem is Faktura wybrana)
            {
                ListaFaktur.Remove(wybrana);
            }
            else
            {
                MessageBox.Show("Najpierw zaznacz fakturę w tabeli!", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnGenerujPdf_Click(object sender, RoutedEventArgs e)
        {
            if (dgFaktury.SelectedItem is Faktura wybranaFaktura)
            {
                GenerujPlikPdfDynamicznie(wybranaFaktura);
            }
            else
            {
                MessageBox.Show("Najpierw wybierz fakturę z listy!", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // --- WYGLĄD PDF: OBSŁUGA KOREKT ---
        private void GenerujPlikPdfDynamicznie(Faktura fakturaDoWydruku)
        {
            string nazwaPliku = $"Faktura_{fakturaDoWydruku.NumerFaktury.Replace("/", "_")}.pdf";
            string sciezkaDoPliku = Path.Combine(Environment.CurrentDirectory, nazwaPliku);

            QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily(Fonts.Arial));

                    page.Header().Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            string tytul = fakturaDoWydruku.IsKorekta ? "FAKTURA KORYGUJĄCA" : "FAKTURA VAT";

                            col.Item().Text(tytul).FontSize(18).Bold();
                            col.Item().Text($"Nr {fakturaDoWydruku.NumerFaktury}").FontSize(12);

                            if (fakturaDoWydruku.IsKorekta)
                            {
                                if (!string.IsNullOrWhiteSpace(fakturaDoWydruku.NumerFakturyKorygowanej))
                                {
                                    col.Item().PaddingTop(5).Text($"Dotyczy faktury nr: {fakturaDoWydruku.NumerFakturyKorygowanej}").FontSize(10).FontColor(Colors.Grey.Darken2);
                                }
                                // Zapisujemy powód korekty na PDFie
                                if (!string.IsNullOrWhiteSpace(fakturaDoWydruku.PowodKorekty))
                                {
                                    col.Item().PaddingTop(2).Text($"Powód korekty: {fakturaDoWydruku.PowodKorekty}").FontSize(10).FontColor(Colors.Grey.Darken2);
                                }
                            }
                        });

                        row.ConstantItem(150).AlignRight().Column(col =>
                        {
                            col.Item().Text("Data wystawienia:");
                            col.Item().Text($"{fakturaDoWydruku.DataWystawienia:dd.MM.yyyy}").Bold();
                        });
                    });

                    page.Content().PaddingVertical(1, Unit.Centimetre).Column(column =>
                    {
                        column.Item().PaddingBottom(20).Row(row =>
                        {
                            row.RelativeItem().Column(col =>
                            {
                                col.Item().PaddingBottom(2).Text("Sprzedawca:").Bold();
                                col.Item().Text("TechWave Sp. z o.o.");
                                col.Item().Text("NIP: 839467298");
                                col.Item().Text("ul. Programistów 1");
                                col.Item().Text("83-867 Gdańsk");
                                col.Item().Text("Polska");
                            });

                            row.RelativeItem().Column(col =>
                            {
                                col.Item().PaddingBottom(2).Text("Nabywca:").Bold();
                                col.Item().Text(fakturaDoWydruku.Nabywca.Nazwa);
                                col.Item().Text($"NIP: {fakturaDoWydruku.Nabywca.NIP}");
                                col.Item().Text(fakturaDoWydruku.Nabywca.Adres);
                                col.Item().Text(fakturaDoWydruku.Nabywca.Kraj);
                            });
                        });

                        column.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.ConstantColumn(30);
                                columns.RelativeColumn(3);
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                            });

                            table.Header(header =>
                            {
                                header.Cell().BorderBottom(1).PaddingBottom(5).Text("L.p.").Bold();
                                header.Cell().BorderBottom(1).PaddingBottom(5).Text("Nazwa usługi/towaru").Bold();
                                header.Cell().BorderBottom(1).PaddingBottom(5).AlignRight().Text("Ilość").Bold();
                                header.Cell().BorderBottom(1).PaddingBottom(5).AlignRight().Text("Cena netto").Bold();
                                header.Cell().BorderBottom(1).PaddingBottom(5).AlignRight().Text("VAT").Bold();
                                header.Cell().BorderBottom(1).PaddingBottom(5).AlignRight().Text("Brutto").Bold();
                            });

                            int numerLp = 1;
                            foreach (var pozycja in fakturaDoWydruku.Pozycje)
                            {
                                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).PaddingVertical(5).Text(numerLp.ToString());
                                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).PaddingVertical(5).Text(pozycja.NazwaTowaruUslugi);
                                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).PaddingVertical(5).AlignRight().Text(pozycja.Ilosc.ToString());
                                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).PaddingVertical(5).AlignRight().Text($"{pozycja.CenaNetto:N2} {fakturaDoWydruku.Waluta}");
                                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).PaddingVertical(5).AlignRight().Text($"{pozycja.StawkaVat}%");
                                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).PaddingVertical(5).AlignRight().Text($"{pozycja.WartoscNetto:N2} {fakturaDoWydruku.Waluta}");
                                numerLp++;
                            }
                        });

                        column.Item().PaddingTop(15).Row(row =>
                        {
                            row.RelativeItem().Column(col =>
                            {
                                col.Item().Text("Dane do przelewu:").Bold();
                                col.Item().Text("Bank: PKO BP");
                                col.Item().Text("Nr konta: 00 1020 3040 5060 7080 9000 0000");
                                col.Item().Text("Termin płatności: 14 dni");
                            });

                            row.ConstantItem(200).Column(col =>
                            {
                                decimal sumaNetto = fakturaDoWydruku.Pozycje.Sum(p => p.WartoscNetto);
                                decimal sumaVat = fakturaDoWydruku.Pozycje.Sum(p => p.WartoscNetto * ((decimal)p.StawkaVat / 100));

                                col.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("Razem netto:");
                                    r.RelativeItem().AlignRight().Text($"{sumaNetto:N2} {fakturaDoWydruku.Waluta}");
                                });
                                col.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("Kwota VAT:");
                                    r.RelativeItem().AlignRight().Text($"{sumaVat:N2} {fakturaDoWydruku.Waluta}");
                                });

                                col.Item().PaddingTop(5).PaddingBottom(5).LineHorizontal(1);

                                col.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("DO ZAPŁATY:").Bold().FontSize(12);
                                    r.RelativeItem().AlignRight().Text($"{fakturaDoWydruku.SumaCalkowita:N2} {fakturaDoWydruku.Waluta}").Bold().FontSize(12);
                                });

                                if (fakturaDoWydruku.Waluta != "PLN")
                                {
                                    col.Item().PaddingTop(10).AlignRight().Text($"Kurs NBP: 1 {fakturaDoWydruku.Waluta} = {fakturaDoWydruku.KursWaluty:N4} PLN").FontSize(9).FontColor(Colors.Grey.Darken2);
                                    decimal sumaPLN = fakturaDoWydruku.SumaCalkowita * fakturaDoWydruku.KursWaluty;
                                    col.Item().AlignRight().Text($"Równowartość: {sumaPLN:N2} PLN").FontSize(10).Bold();
                                }
                            });
                        });
                    });

                    page.Footer().Column(col =>
                    {
                        col.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                        col.Item().PaddingTop(5).Row(row =>
                        {
                            row.RelativeItem().Text("Dziękujemy za współpracę!").FontColor(Colors.Grey.Medium).FontSize(9);
                            row.RelativeItem().AlignRight().Text(x =>
                            {
                                x.Span("Strona ").FontSize(9).FontColor(Colors.Grey.Medium);
                                x.CurrentPageNumber().FontSize(9).FontColor(Colors.Grey.Medium);
                                x.Span(" z ").FontSize(9).FontColor(Colors.Grey.Medium);
                                x.TotalPages().FontSize(9).FontColor(Colors.Grey.Medium);
                            });
                        });
                    });
                });
            })
            .GeneratePdf(sciezkaDoPliku);

            try
            {
                Process.Start(new ProcessStartInfo(sciezkaDoPliku) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Błąd otwierania PDF: {ex.Message}", "Uwaga");
            }
        }

        // --- ZAKŁADKA 3: KREATOR / EDYTOR ---

        private void ChkKorekta_Zmieniono(object sender, RoutedEventArgs e)
        {
            bool isChecked = chkKorekta.IsChecked == true;

            if (txtKorygowanaFaktura != null) txtKorygowanaFaktura.IsEnabled = isChecked;
            if (txtPowodKorekty != null) txtPowodKorekty.IsEnabled = isChecked;

            if (!isChecked)
            {
                if (txtKorygowanaFaktura != null) txtKorygowanaFaktura.Clear();
                if (txtPowodKorekty != null) txtPowodKorekty.Clear();
            }
        }

        private async void CmbWaluta_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbWaluta == null || txtKursWaluty == null) return;

            var wybranaWaluta = (cmbWaluta.SelectedItem as ComboBoxItem)?.Content.ToString();

            if (wybranaWaluta == "PLN")
            {
                txtKursWaluty.Text = "1";
                AktualizujSume();
                return;
            }

            if (_edytowanaFaktura != null && _edytowanaFaktura.Waluta == wybranaWaluta)
            {
                AktualizujSume();
                return;
            }

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    string url = $"http://api.nbp.pl/api/exchangerates/rates/a/{wybranaWaluta.ToLower()}/?format=json";
                    string json = await client.GetStringAsync(url);

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var nbpData = JsonSerializer.Deserialize<NbpResponse>(json, options);

                    if (nbpData != null && nbpData.Rates.Count > 0)
                    {
                        txtKursWaluty.Text = nbpData.Rates[0].Mid.ToString("0.0000");
                    }
                }
            }
            catch (Exception)
            {
                MessageBox.Show("Nie udało się pobrać kursu waluty z NBP. Sprawdź swoje połączenie z internetem.", "Błąd pobierania", MessageBoxButton.OK, MessageBoxImage.Error);
                txtKursWaluty.Text = "0,0000";
            }

            AktualizujSume();
        }

        private void BtnDodajPozycje_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtNazwaTowaru.Text) ||
                !int.TryParse(txtIloscTowaru.Text, out int ilosc) ||
                !decimal.TryParse(txtCenaTowaruNetto.Text, out decimal cena) ||
                !int.TryParse(txtStawkaVat.Text, out int vat))
            {
                MessageBox.Show("Sprawdź poprawność wprowadzonych danych.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TymczasowePozycje.Add(new PozycjaFaktury
            {
                Id = TymczasowePozycje.Count + 1,
                NazwaTowaruUslugi = txtNazwaTowaru.Text,
                Ilosc = ilosc,
                CenaNetto = cena,
                StawkaVat = vat
            });

            txtNazwaTowaru.Clear();
            txtCenaTowaruNetto.Clear();
            txtIloscTowaru.Text = "1";
            AktualizujSume();
        }

        private void BtnUsunPozycje_Click(object sender, RoutedEventArgs e)
        {
            if (dgPozycje.SelectedItem is PozycjaFaktury wybranaPozycja)
            {
                TymczasowePozycje.Remove(wybranaPozycja);
                AktualizujSume();
            }
            else
            {
                MessageBox.Show("Najpierw zaznacz pozycję w tabeli, którą chcesz usunąć!", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AktualizujSume()
        {
            string waluta = (cmbWaluta.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "PLN";
            decimal suma = TymczasowePozycje.Sum(p => p.WartoscBrutto);
            txtSuma.Text = $"Suma: {suma:N2} {waluta}";
        }

        private void BtnZapiszFakture_Click(object sender, RoutedEventArgs e)
        {
            if (cmbKontrahenci.SelectedItem == null)
            {
                MessageBox.Show("Musisz wybrać nabywcę!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TymczasowePozycje.Count == 0)
            {
                MessageBox.Show("Faktura musi mieć chociaż jedną pozycję!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!decimal.TryParse(txtKursWaluty.Text, out decimal kursWaluty) || kursWaluty <= 0)
            {
                MessageBox.Show("Kurs waluty jest nieprawidłowy.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var wybranyNabywca = (Kontrahent)cmbKontrahenci.SelectedItem;
            string wybranaWaluta = (cmbWaluta.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "PLN";

            if (_edytowanaFaktura != null)
            {
                _edytowanaFaktura.NumerFaktury = txtNumerFaktury.Text;
                _edytowanaFaktura.DataWystawienia = dpDataWystawienia.SelectedDate ?? DateTime.Now;
                _edytowanaFaktura.Nabywca = wybranyNabywca;
                _edytowanaFaktura.KontrahentId = wybranyNabywca.Id;
                _edytowanaFaktura.Waluta = wybranaWaluta;
                _edytowanaFaktura.KursWaluty = kursWaluty;
                _edytowanaFaktura.IsKorekta = chkKorekta.IsChecked == true;
                _edytowanaFaktura.NumerFakturyKorygowanej = txtKorygowanaFaktura.Text;
                _edytowanaFaktura.PowodKorekty = txtPowodKorekty.Text;
                _edytowanaFaktura.Pozycje = TymczasowePozycje.ToList();

                dgFaktury.Items.Refresh();
                MessageBox.Show("Zmiany w fakturze zostały zapisane!", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                int noweId = ListaFaktur.Count > 0 ? ListaFaktur.Max(f => f.Id) + 1 : 1;

                var nowaFaktura = new Faktura
                {
                    Id = noweId,
                    NumerFaktury = txtNumerFaktury.Text,
                    DataWystawienia = dpDataWystawienia.SelectedDate ?? DateTime.Now,
                    Nabywca = wybranyNabywca,
                    KontrahentId = wybranyNabywca.Id,
                    Waluta = wybranaWaluta,
                    KursWaluty = kursWaluty,
                    IsKorekta = chkKorekta.IsChecked == true,
                    NumerFakturyKorygowanej = txtKorygowanaFaktura.Text,
                    PowodKorekty = txtPowodKorekty.Text,
                    Pozycje = TymczasowePozycje.ToList()
                };

                ListaFaktur.Add(nowaFaktura);
                MessageBox.Show("Nowa faktura została wystawiona!", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            _edytowanaFaktura = null;
            TymczasowePozycje.Clear();
            txtNumerFaktury.Text = "FV ";
            cmbKontrahenci.SelectedItem = null;
            cmbWaluta.SelectedIndex = 0;
            chkKorekta.IsChecked = false;
            txtKorygowanaFaktura.Clear();
            txtPowodKorekty.Clear();
            AktualizujSume();

            MainTabControl.SelectedIndex = 1;
        }
    }
}