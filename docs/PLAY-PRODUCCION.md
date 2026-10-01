# Solicitud de acceso a producción en Google Play — PDF Reader

Respuestas para el cuestionario de Play Console › **Panel › Solicitar acceso a producción**, en
catalán (el idioma de la consola). Cada texto cabe en los 300 caracteres del formulario; el número
entre paréntesis es su longitud. Constitución Mobile §11. **Última actualización: 2026-10-01**
(versión 2026.09.30.0).

> Lo marcado con ⚠ no lo puedo saber yo: compruébalo en la consola antes de enviarlo y cámbialo si
> no es así.

---

## Informació sobre la prova tancada

**Com has reclutat usuaris per a la prova tancada?** (259)

```
He afegit a la prova tancada quatre grups públics de Google de verificadors voluntaris (comunitats d'intercanvi de proves de 12 persones durant 14 dies). No he fet servir cap proveïdor de pagament. També l'he provada jo mateix en un mòbil real amb Android 16.
```

**Fins a quin punt t'ha resultat fàcil reclutar verificadors?** — Propuesta: **Ni fàcil ni difícil**
(los grupos públicos dan el número, pero participan poco).

**Descriu la implicació dels verificadors** (268) ⚠ *comprueba en Estadísticas / Prova tancada que
de verdad la abrieron; si no hay datos, quita la parte de las funciones.*

```
Els verificadors han instal·lat l'app i l'han oberta amb PDF propis: obrir documents, passar pàgines, zoom, cercar text i la biblioteca de recents. És l'ús que espero d'un usuari real: llegir PDF sense connexió. No han fet servir gaire la contrasenya de PDF protegits.
```

**Resum dels suggeriments i com els has recollit** (256) ⚠ *si algún verificador dejó comentarios
(en la consola o por correo), menciónalos.*

```
Pocs comentaris escrits dels verificadors; els he recollit des de la consola de Play i GitHub. Les millores han sortit sobretot de les meves proves en un mòbil real i del banc de proves automàtiques: el botó enrere a Android 16 i les pàgines molt llargues.
```

## Informació sobre l'aplicació

**A quin públic objectiu va dirigida?** (182)

```
Qualsevol persona que vulgui llegir PDF al mòbil de forma senzilla i privada: sense anuncis, sense compte, sense connexió i sense demanar cap permís. Estudiants, feina i ús personal.
```

**Com proporciona valor als usuaris?** (196)

```
Obre i llegeix PDF ràpid, amb zoom, cerca de text, anar a una pàgina i continuar on ho vas deixar. Tot es queda al dispositiu: no demana permisos, no té anuncis ni rastrejadors i el codi és obert.
```

**Instal·lacions esperades el primer any** — Propuesta: **0 - 10.000** (app nueva, sin promoción).

## Preparació per a la producció

**Quins canvis has fet en funció de la prova tancada?** (250)

```
He arreglat el botó enrere a Android 16 (tornava a tancar l'app), he afegit un gestor d'errors perquè cap error inesperat la tanqui, he evitat un tancament per falta de memòria en pàgines molt llargues i he afegit 69 proves automàtiques de la lògica.
```

**Com has decidit que està preparada per a producció?** (252) ⚠ *comprueba en Qualitat › Android
Vitals que no hay fallos; si los hay, quita «sense tancaments a la consola».*

```
Les proves automàtiques passen totes, l'he provada en un mòbil real amb Android 16 i lletra gran sense errors, els verificadors l'han fet servir 14 dies sense tancaments a la consola i la fitxa, la privadesa i la seguretat de les dades estan completes.
```
