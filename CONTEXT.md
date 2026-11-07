# Video Production

Video Production описывает подготовку одного пользовательского материала, создание различающихся монтажных вариантов и доставку проверенного результата.

## Language

**Job**:
Один запрос пользователя на производство набора видео из одного исходного материала.
_Avoid_: Task, request, batch

**Variant**:
Один самостоятельно воспроизводимый результат монтажа внутри Job.
_Avoid_: Version, copy

**Artifact**:
Неизменяемый входной, промежуточный или итоговый файл, известный системе по идентификатору.
_Avoid_: Blob, arbitrary file

**Revision**:
Неизменяемая редакция анализа, плана или утверждаемого документа.
_Avoid_: Mutable version

**Original**:
Неизменённый материал, полученный от пользователя.
_Avoid_: Source file

**Master**:
Нормализованный или очищенный материал, из которого разрешено строить монтаж.
_Avoid_: Original, output

**Allowed Segment**:
Утверждённое анализом сопоставление интервала Original с интервалом Master, которое можно использовать в монтаже.
_Avoid_: Arbitrary clip, invented timestamp

**Edit Plan**:
Версионированное детерминированное описание одного Variant, ссылающееся только на известные Artifacts и Allowed Segments.
_Avoid_: Prompt, React composition

**Output Timeline**:
Последовательность кадров итогового Variant.
_Avoid_: Original timeline, master timeline

**Stage**:
Предметный этап производства, например Analysis, Planning, Rendering или Delivery.
_Avoid_: Status

**Status**:
Положение Job относительно выполнения: ожидает, выполняется, ждёт подтверждения или завершено.
_Avoid_: Stage
