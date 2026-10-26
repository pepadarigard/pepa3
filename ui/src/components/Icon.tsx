import {
  Box,
  Copy,
  FileInput,
  FilePenLine,
  FileSearch,
  FileText,
  Flag,
  FolderPlus,
  GitBranch,
  ListTree,
  Play,
  ScrollText,
  Timer,
  Trash2,
  Variable,
  type LucideIcon,
  type LucideProps,
} from "lucide-react";

/** Значки блоков по имени из описания типа (только используемые — чтобы не тянуть весь набор). */
const nodeIcons: Record<string, LucideIcon> = {
  play: Play,
  flag: Flag,
  "git-branch": GitBranch,
  variable: Variable,
  "scroll-text": ScrollText,
  timer: Timer,
  "file-pen-line": FilePenLine,
  "file-text": FileText,
  copy: Copy,
  "file-input": FileInput,
  "trash-2": Trash2,
  "folder-plus": FolderPlus,
  "file-search": FileSearch,
  "list-tree": ListTree,
  box: Box,
};

export function NodeIcon({ name, ...props }: { name: string } & LucideProps) {
  const Component = nodeIcons[name] ?? Box;
  return <Component {...props} />;
}

export function categoryColor(category: string): string {
  switch (category) {
    case "logic":
      return "var(--cat-logic)";
    case "files":
      return "var(--cat-files)";
    default:
      return "var(--cat-other)";
  }
}
