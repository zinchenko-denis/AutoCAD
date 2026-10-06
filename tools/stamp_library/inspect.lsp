; Isolated read-only stamp diagnostic. Not a production stamp editor.
; Command: ATFSTAMPINSPECT. Full AutoCAD for Windows is required for COM.

(defun atsi:emit (message)
  (princ (strcat "\n" message))
  (if atsi:log (write-line message atsi:log)))

(defun atsi:show (name value)
  (atsi:emit (strcat name " = " (vl-prin1-to-string value))))

(defun atsi:open-new (path / result)
  ; This is the only output-file open. Existing paths are always refused.
  (cond
    ((not (member (getvar "LISPSYS") '(1 2)))
      ; Keep this fallback ASCII-readable even in the legacy Lisp engine.
      (princ "\nLISPSYS is not 1/2: F2 only; UTF-8 TXT disabled.") nil)
    ((or (null path) (= path "")) nil)
    ((/= (strcase (cond ((vl-filename-extension path)) (T ""))) ".TXT")
      (princ "\nНужно расширение TXT; вывод только в F2.") nil)
    ((or (findfile path) (vl-file-directory-p path))
      (princ "\nСуществующий путь отклонён; вывод только в F2.") nil)
    (T
      (setq result (vl-catch-all-apply 'open (list path "w" "utf8-bom")))
      (cond
        ((vl-catch-all-error-p result)
          (princ (strcat "\nОшибка открытия TXT; вывод только в F2: "
            (vl-catch-all-error-message result))) nil)
        ((null result) (princ "\nTXT не открыт; вывод только в F2.") nil)
        (T result)))))

(defun atsi:unbox (value)
  (cond
    ((= (type value) 'VARIANT) (atsi:unbox (vlax-variant-value value)))
    ((= (type value) 'SAFEARRAY)
      (mapcar 'atsi:unbox (vlax-safearray->list value)))
    (T value)))

(defun atsi:properties (object / property available value)
  ; Availability is checked without the optional writable-property flag.
  (foreach property
    '(ObjectName Handle OwnerID ObjectID Layer Name EffectiveName
      TextString TagString PromptString Constant Invisible MTextAttribute
      MTextAttributeContent HasAttributes IsDynamicBlock InsertionPoint
      Alignment TextAlignmentPoint Height Width Rotation ScaleFactor
      XScaleFactor YScaleFactor ZScaleFactor StyleName HasExtensionDictionary)
    (setq available (vl-catch-all-apply 'vlax-property-available-p
      (list object property)))
    (if (and (not (vl-catch-all-error-p available)) available)
      (progn
        (setq value (vl-catch-all-apply 'vlax-get-property (list object property)))
        (if (vl-catch-all-error-p value)
          (atsi:show (strcat "COM." (vl-symbol-name property) ".ERROR")
            (vl-catch-all-error-message value))
          (progn
            (setq value (vl-catch-all-apply 'atsi:unbox (list value)))
            (if (vl-catch-all-error-p value)
              (atsi:show "COM.VALUE.ERROR" (vl-catch-all-error-message value))
              (atsi:show (strcat "COM." (vl-symbol-name property)) value))))))))

(defun atsi:attributes (object method / applicable result attribute count)
  ; The method gate is deliberately closed to every other COM method.
  (if (member method '(GetAttributes GetConstantAttributes))
    (progn
      (atsi:emit (strcat "ATTRIBUTES " (vl-symbol-name method)))
      (setq applicable (vl-catch-all-apply 'vlax-method-applicable-p
        (list object method)))
      (if (and (not (vl-catch-all-error-p applicable)) applicable)
        (progn
          (setq result (vl-catch-all-apply 'vlax-invoke-method (list object method)))
          (if (not (vl-catch-all-error-p result))
            (setq result (vl-catch-all-apply 'atsi:unbox (list result))))
          (if (vl-catch-all-error-p result)
            (atsi:show "ATTRIBUTE_READ_ERROR" (vl-catch-all-error-message result))
            (progn
              (setq count 0)
              (foreach attribute result
                (setq count (1+ count))
                (atsi:show "ATTRIBUTE_INDEX" count)
                (atsi:properties attribute))
              (atsi:show "ATTRIBUTE_COUNT" count))))
        (atsi:emit "METHOD_UNAVAILABLE")))))

(defun atsi:owners (entity / data owner seen depth)
  (setq data (entget entity) seen (list entity) depth 0)
  (while (and data (setq owner (cdr (assoc 330 data)))
              (< depth 16) (not (member owner seen)))
    (setq seen (cons owner seen) depth (1+ depth) data (entget owner))
    (atsi:show "OWNER_CHAIN"
      (list depth (assoc 0 data) (assoc 5 data) (assoc 2 data) (assoc 410 data))))
  (if (and data (cdr (assoc 330 data)))
    (atsi:emit "OWNER_CHAIN_STOP: cycle or depth limit; do not infer layout.")))

(defun atsi:entity (entity label / data pair kind object result)
  (atsi:emit (strcat "--- " label " ---"))
  (setq data (entget entity))
  (if data
    (progn
      (setq kind (cdr (assoc 0 data)))
      (atsi:show "DXF_TYPE" kind)
      ; Preserve all MTEXT group-3 chunks and subclass markers in source order.
      (foreach pair data
        (if (member (car pair) '(0 1 2 3 5 8 10 11 40 41 50 66 67 70 100 102 330 340 350 360 410))
          (atsi:show "DXF" pair)))
      (atsi:owners entity)
      (if (not (member kind
        '("TEXT" "MTEXT" "ATTRIB" "ATTDEF" "INSERT" "LINE" "LWPOLYLINE"
          "POLYLINE" "ARC" "CIRCLE" "ELLIPSE" "SPLINE" "HATCH" "SOLID"
          "POINT" "VIEWPORT" "DIMENSION")))
        (atsi:emit "UNSUPPORTED_CLASS: proxy/custom/SPDS semantics are not decoded."))
      (if atsi:com
        (progn
          (setq object (vl-catch-all-apply 'vlax-ename->vla-object (list entity)))
          (if (vl-catch-all-error-p object)
            (atsi:show "COM_UNAVAILABLE" (vl-catch-all-error-message object))
            (progn
              (atsi:properties object)
              (if (= kind "INSERT")
                (progn
                  (atsi:attributes object 'GetAttributes)
                  (atsi:attributes object 'GetConstantAttributes)))
              (atsi:emit "COM_DUMP: полный список ниже только в F2; методы перечисляются, но не вызываются.")
              (setq result (vl-catch-all-apply 'vlax-dump-object (list object T)))
              (if (vl-catch-all-error-p result)
                (atsi:show "COM_DUMP_ERROR" (vl-catch-all-error-message result))))))))
    (atsi:emit "ENTITY_UNAVAILABLE")))

(defun c:ATFSTAMPINSPECT (/ *error* atsi:log atsi:com path result role picked container index)
  (defun *error* (message)
    (if atsi:log (vl-catch-all-apply 'close (list atsi:log)))
    (setq atsi:log nil)
    (princ (strcat "\nЧтение остановлено: " message ". Открытый частичный TXT закрыт."))
    (princ))
  (setq result (vl-catch-all-apply 'vl-load-com nil)
        atsi:com (not (vl-catch-all-error-p result)))
  (if (member (getvar "LISPSYS") '(1 2))
    (setq path (getfiled "Новый TXT штампа (Отмена = только F2)"
      (strcat (getvar "TEMPPREFIX") "stamp_inspect_" (rtos (getvar "CDATE") 2 6) ".txt")
      "txt" 1)))
  (setq atsi:log (atsi:open-new path))
  (atsi:emit "ATFSTAMPINSPECT: только чтение; соответствие полей штампа не предполагается.")
  (atsi:show "DRAWING" (strcat (getvar "DWGPREFIX") (getvar "DWGNAME")))
  (atsi:show "ACADVER" (getvar "ACADVER"))
  (atsi:show "CTAB" (getvar "CTAB"))
  (atsi:show "LISPSYS" (getvar "LISPSYS"))
  (atsi:show "COM_AVAILABLE" atsi:com)
  (if atsi:log
    (progn (atsi:show "TXT_PATH" path) (atsi:show "TXT_ENCODING" "utf8-bom"))
    (atsi:emit "TXT недоступен; вывод только в F2."))
  (foreach role '(("PROJECT_CODE" "Шифр") ("ADDRESS" "Адрес") ("FRAME_OR_STAMP" "Штамп"))
    (setq picked (nentselp (strcat "\nВыберите: " (cadr role) " (вложенный выбор; Enter = пропуск): ")))
    (if picked
      (progn
        (atsi:show "SELECTION_ROLE" (car role))
        (atsi:show "PICK_POINT" (cadr picked))
        (atsi:entity (car picked) "SELECTED_ENTITY")
        (if (> (length picked) 2)
          (progn
            (atsi:show "NESTED_TRANSFORM_RAW" (caddr picked))
            (setq index 0)
            (foreach container (cadddr picked)
              (setq index (1+ index))
              (atsi:entity container (strcat "NESTED_CONTAINER_" (itoa index)))))))
      (atsi:show "SKIPPED_ROLE" (car role))))
  (atsi:emit "END. Передайте TXT и полный вывод F2: дополнительные свойства СПДС находятся только в F2.")
  (if atsi:log (close atsi:log))
  (setq atsi:log nil)
  (princ))

(princ "\nИнспектор только для чтения загружен. Команда: ATFSTAMPINSPECT.")
(princ)
