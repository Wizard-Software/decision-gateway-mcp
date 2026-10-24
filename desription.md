
Oto krótka notatka koncepcyjna:

### Decision-Gateway MCP – Koncepcja Działania

**1. Cel**
Udostępnienie ujednoliconego narzędzia MCP do szybkiego podejmowania decyzji klasyfikacyjnych, agregującego API Jev oraz OpenAI Decisions pod jednym interfejsem.

**2. Ujednolicony Kontrakt Wejściowy (Tool Schema)**
Serwer rejestruje narzędzie (np. `evaluate_decision`) z następującymi parametrami wejściowymi:

* `query` (string, wymagane): Kontekst lub pytanie decyzyjne.
* `options` (array of strings, wymagane): Dopuszczalne warianty odpowiedzi.
* `provider` (enum: `jev` | `openai`, wymagane): Wybór silnika decyzyjnego.
* `image_payload` (string/base64, opcjonalne): Obraz do analizy wielomodalnej.

**3. Warstwa Walidacji i Obsługa Asymetrii (Obrazki)**
Brama weryfikuje żądanie przed nawiązaniem połączenia HTTP:

* **Ścieżka OpenAI:** Jeśli `provider = openai`, a `image_payload` jest obecny, system buduje payload wielomodalny. Jeśli obrazu brak – wysyła sam tekst.
* **Ścieżka Jev:** Jeśli `provider = jev` i `image_payload` jest puste, żądanie przechodzi normalnie. Jeśli `image_payload` zawiera dane, system natychmiast odrzuca wywołanie, zwracając do MCP błąd w stylu:
*„Validation Error: Model Jev nie obsługuje obrazów. Zmień provider na 'openai' lub usuń parametr 'image_payload'.”*

**4. Wykonanie i Wynik (Routing)**
W zależności od wybranego providera, żądanie trafia do odpowiedniego klienta (implementującego wspólny interfejs, np. `IDecisionClient`). Obaj dostawcy zwracają ujednolicone wyjście, które brama przekazuje z powrotem do MCP:

* `selected_option` (string)
* `confidence` (float 0.0 - 1.0)
* `latency_ms` (int, przydatne do monitoringu)

Taka konstrukcja pozwala nadrzędnemu modelowi wywołać Jev do błyskawicznych, tanich decyzji tekstowych, a w razie napotkania obrazka w kontekście – inteligentnie przełączyć się na OpenAI Decisions (bądź natychmiast poprawić swój błąd dzięki czytelnemu wyjątkowi walidacji).
