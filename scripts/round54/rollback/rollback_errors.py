"""Shared command-line refusal type for the Round 54 rollback tools."""


class Refusal(RuntimeError):
    """An expected fail-closed rejection caused by input or state not meeting contract."""
