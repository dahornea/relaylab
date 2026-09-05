# Pornire RelayLab în Codex Desktop

Acest document păstrează pașii inițiali ai pachetului bootstrap. M1 este acum implementat; pentru pornirea aplicației folosește [README.md](README.md), iar pentru verificările efective consultă [docs/STATUS.md](docs/STATUS.md). Documentele tehnice și prompturile sunt în engleză.

## 1. Pregătește repository-ul

Dezarhivează pachetul într-un director separat. Conținutul directorului relaylab-bootstrap trebuie să ajungă la rădăcina repository-ului relaylab, astfel încât AGENTS.md și README.md să fie lângă directorul .codex.

Dacă ai deja clonat repository-ul de pe GitHub, păstrează directorul .git, licența existentă și modificările proprii. Compară fișierele care au deja același nume înainte să le înlocuiești. Nu copia peste sourcegen-auditor. Dacă RelayLab are deja implementare, cere integrarea pachetului cu starea actuală, fără regenerarea codului.

Pachetul este noua bază pentru proiect și înlocuiește cele două fișiere standalone RelayLab oferite anterior. Nu le adăuga și pe acelea ca instrucțiuni concurente.

## 2. Deschide proiectul în Desktop

Alege GPT-6 Astra în conversația proiectului. Configurația inclusă folosește gpt-6-astra cu reasoning high; verifică selecția efectivă din aplicație. Configurația nu oferă acces la un model indisponibil contului. Păstrează permisiunile normale de lucru ale aplicației.

Configurația proiectului trebuie încărcată prin mecanismul normal de trust al clientului. Deschide o sesiune nouă după copierea fișierelor. Detalii și limite: [docs/CODEX.md](docs/CODEX.md).

Pentru implementare sunt necesare .NET 10 compatibil, Git și Docker cu Linux containers; Windows poate folosi Docker Desktop/WSL2 conform configurației tale. Scripturile M1 din eng/ necesită PowerShell 7. Versiunile selectate și verificate sunt în docs/STATUS.md. Nu instala automat un SDK nou doar pentru a reproduce pin-ul SourceGen Auditor.

## 3. Trimite promptul inițial

Copiază integral [prompts/01-IMPLEMENT-M1.md](prompts/01-IMPLEMENT-M1.md) într-o conversație care permite implementarea. Alternativ, folosește exact acest mesaj:

```text
Read AGENTS.md and prompts/01-IMPLEMENT-M1.md in full, then carry out that M1 task. Use GPT-6 Astra and the configured read-only roles as described. These bootstrap documents replace the earlier standalone RelayLab brief/prompt. Implement and verify M1 only, leaving the result uncommitted.
```

Codex trebuie să prezinte un plan scurt și să înceapă implementarea. Nu este necesară o nouă aprobare pentru fiecare alegere normală de cod sau pentru corectarea unui test. Un conflict real de contract, accesul lipsă sau o acțiune externă neautorizată pot necesita o întrebare punctuală.

## 4. Continuarea

- După rezultatul M1: revizuiești demo-ul, verificările și explicațiile. Dacă alegi să continui, folosești [promptul M2](prompts/02-IMPLEMENT-M2.md).
- După M2: [promptul M3](prompts/03-PREPARE-M3.md) pregătește deployment-ul. Aprobi ulterior un plan concret de resurse/cost înainte de provisioning.
- După întrerupere: [promptul de reluare](prompts/04-RESUME.md).

Nu trimite toate prompturile simultan. Commit, push și publicare rămân acțiuni separate, când le ceri explicit.

## Ce face configurația mai eficientă

AGENTS.md stabilește autonomia și verificarea proporțională; documentele sunt citite după nevoie. Agentul principal implementează. Trei roluri read-only ajută cu research, test design și review, cel mult două simultan. Nu există hook-uri custom, pseudo-gates, instalații MCP obligatorii sau un frontend ascuns în scope.

Designul experienței API/demo și conceptul opțional de inspector sunt în [docs/DESIGN.md](docs/DESIGN.md). Inspectorul nu este implementare autorizată în M1–M3.

Verificările efectuate asupra pachetului și limitele lor sunt în [BOOTSTRAP-VERIFICATION.md](BOOTSTRAP-VERIFICATION.md).
