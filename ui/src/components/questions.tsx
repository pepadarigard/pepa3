import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { MessageCircleQuestionMark, Send } from "lucide-react";
import { api } from "../api/client";
import { useQuestions } from "../api/hooks";
import type { Question } from "../api/types";
import { formatMoment, relative } from "../lib/format";
import { navigate } from "../router";
import { useFeedback } from "./feedback";
import { Button, Card } from "./ui";

/**
 * Вопросы процессов («Спросить пользователя»), которые ждут ответа: все (на «Главной») или одного процесса.
 * Ответ уходит один раз: повторное нажатие или вторая вкладка не дадут второго ответа.
 */
export function QuestionsCard({ processId }: { processId?: string }) {
  const questions = useQuestions(processId);
  const items = questions.data ?? [];
  if (items.length === 0) return null;
  return (
    <Card
      className="questions-card"
      title={
        <h2 className="row">
          <MessageCircleQuestionMark size={17} style={{ color: "var(--attention)" }} />
          {processId ? "Процесс спрашивает" : `Вопросы процессов · ${items.length}`}
        </h2>
      }
    >
      <div className="stack" style={{ padding: 14, gap: 14 }}>
        {items.map((q) => <QuestionItem key={q.id} question={q} showProcess={!processId} />)}
      </div>
    </Card>
  );
}

function QuestionItem({ question, showProcess }: { question: Question; showProcess: boolean }) {
  const feedback = useFeedback();
  const client = useQueryClient();
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);

  const send = async (answer: { answer?: string; option?: number }) => {
    setBusy(true);
    try {
      const result = await api.answer(question.id, answer);
      feedback.toast(result.duplicate ? "На этот вопрос уже ответили — второй ответ не отправлен." : "Ответ отправлен.",
        result.duplicate ? "info" : "success");
    } catch (e) {
      feedback.error(e);
    } finally {
      setBusy(false);
      client.invalidateQueries({ queryKey: ["questions"] });
      client.invalidateQueries({ queryKey: ["process", question.process.id] });
      client.invalidateQueries({ queryKey: ["overview"] });
    }
  };

  return (
    <div className="question" data-testid="question">
      {showProcess && (
        <div className="small muted">
          <a href={`#/process/${question.process.id}`} onClick={(e) => { e.preventDefault(); navigate({ page: "process", id: question.process.id }); }}>
            «{question.process.scenarioName}» · #{question.process.shortId}
          </a>
          {" · "}спрошено {relative(question.askedAt)}
          {question.expiresAt && <> · ждёт до {formatMoment(question.expiresAt)}</>}
        </div>
      )}
      <div className="question-text">{question.text}</div>
      {question.hasImage && <QuestionImage id={question.id} />}
      {question.note && <div className="small" style={{ color: "var(--attention)" }}>{question.note}</div>}
      {question.options.length > 0 && (
        <div className="row wrap">
          {question.options.map((option, index) => (
            <Button key={index} size="small" variant="primary" disabled={busy} onClick={() => send({ option: index })}>{option}</Button>
          ))}
        </div>
      )}
      {question.freeText && (
        <form className="row" onSubmit={(e) => { e.preventDefault(); if (text.trim()) void send({ answer: text }); }}>
          <input
            className="input"
            type={question.secret ? "password" : "text"}
            autoComplete="off"
            aria-label="Ответ"
            placeholder={question.secret ? "Ответ (не сохранится в истории)" : question.options.length > 0 ? "Или свой ответ" : "Ответ"}
            value={text}
            onChange={(e) => setText(e.target.value)}
            style={{ flex: 1 }}
          />
          <Button type="submit" size="small" icon={Send} busy={busy} disabled={!text.trim()}>Ответить</Button>
        </form>
      )}
    </div>
  );
}

function QuestionImage({ id }: { id: number }) {
  const [url, setUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let revoked = false;
    let created: string | null = null;
    api.questionImage(id).then(
      (value) => {
        created = value;
        if (revoked) URL.revokeObjectURL(value);
        else setUrl(value);
      },
      (e: unknown) => setError(e instanceof Error ? e.message : String(e)),
    );
    return () => {
      revoked = true;
      if (created) URL.revokeObjectURL(created);
    };
  }, [id]);
  if (error) return <div className="small muted">Картинка не открылась: {error}</div>;
  if (!url) return null;
  return <img className="question-image" src={url} alt="Картинка к вопросу" />;
}
