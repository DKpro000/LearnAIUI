import os
from pathlib import Path

import numpy as np
import pandas as pd
import torch
from torch.utils.data import Dataset, TensorDataset, random_split
from torchvision import datasets, transforms


BACKEND_DIR = Path(__file__).resolve().parent
DATASET_ROOT = Path(
    os.environ.get("NN_BUILDER_LOCAL_DATASET_DIR", BACKEND_DIR / "dataset")
)


class TabularClassificationDataset(Dataset):
    def __init__(self, csv_path, target_column, drop_columns=None):
        self.csv_path = Path(csv_path)
        self.target_column = target_column
        self.drop_columns = drop_columns or []

        df = pd.read_csv(self.csv_path)

        if target_column not in df.columns:
            raise RuntimeError(
                f"Target column '{target_column}' not found in {csv_path}. "
                f"Available columns: {list(df.columns)}"
            )

        y = df[target_column]

        x = df.drop(columns=[target_column])

        existing_drop_columns = [
            col for col in self.drop_columns
            if col in x.columns
        ]

        if existing_drop_columns:
            x = x.drop(columns=existing_drop_columns)

        # Fill missing values.
        for col in x.columns:
            if x[col].dtype == object:
                x[col] = x[col].fillna("Unknown")
            else:
                x[col] = x[col].fillna(x[col].median())

        # One-hot encode string columns.
        x = pd.get_dummies(x)

        # Convert labels.
        if y.dtype == object:
            y = y.fillna("Unknown")
            classes = sorted(y.unique().tolist())
            self.class_to_index = {name: i for i, name in enumerate(classes)}
            y = y.map(self.class_to_index)
        else:
            y = y.fillna(0).astype(int)
            self.class_to_index = None

        self.x = torch.tensor(x.values, dtype=torch.float32)
        self.y = torch.tensor(y.values, dtype=torch.long)

        self.input_dim = self.x.shape[1]
        self.num_classes = int(self.y.max().item()) + 1

    def __len__(self):
        return self.x.shape[0]

    def __getitem__(self, index):
        return self.x[index], self.y[index]


def find_first_csv(folder: Path):
    csv_files = list(folder.rglob("*.csv"))

    if len(csv_files) == 0:
        raise RuntimeError(f"No CSV file found in {folder}")

    return csv_files[0]


def build_local_dataset(dataset_name: str, image_size=224):
    if dataset_name == "ChihuahuaMuffin":
        return build_chihuahua_muffin_dataset(image_size=image_size)

    if dataset_name == "Titanic":
        return build_titanic_dataset()

    if dataset_name == "WeatherPrediction":
        return build_weather_prediction_dataset()

    raise RuntimeError(f"Unsupported local dataset: {dataset_name}")


def build_chihuahua_muffin_dataset(image_size=224):
    folder = DATASET_ROOT / "chiwawa_muffin"

    if not folder.exists():
        raise RuntimeError(f"Dataset folder not found: {folder}")

    transform = transforms.Compose([
        transforms.Resize((image_size, image_size)),
        transforms.ToTensor(),
    ])

    # Try common structure:
    # chiwawa_muffin/train/class_a/*.jpg
    # chiwawa_muffin/train/class_b/*.jpg
    train_folder = folder / "train"

    if train_folder.exists():
        dataset_folder = train_folder
    else:
        dataset_folder = folder

    dataset = datasets.ImageFolder(
        root=str(dataset_folder),
        transform=transform,
    )

    return dataset


def build_titanic_dataset():
    folder = DATASET_ROOT / "titanic"

    if not folder.exists():
        raise RuntimeError(f"Dataset folder not found: {folder}")

    csv_path = find_first_csv(folder)

    # Common Titanic target column is Survived.
    dataset = TabularClassificationDataset(
        csv_path=csv_path,
        target_column="Survived",
        drop_columns=[
            "PassengerId",
            "Name",
            "Ticket",
            "Cabin"
        ],
    )

    return dataset


def build_weather_prediction_dataset():
    folder = DATASET_ROOT / "weather_prediction"

    if not folder.exists():
        raise RuntimeError(f"Dataset folder not found: {folder}")

    csv_path = find_first_csv(folder)
    df = pd.read_csv(csv_path)

    # Try common target columns.
    possible_targets = [
        "weather",
        "Weather",
        "RainTomorrow",
        "rain",
        "Rain",
        "target",
        "label",
        "Label"
    ]

    target_column = None

    for col in possible_targets:
        if col in df.columns:
            target_column = col
            break

    if target_column is None:
        raise RuntimeError(
            "Could not auto-detect target column for WeatherPrediction. "
            f"Available columns: {list(df.columns)}. "
            "Please set target column manually in local_datasets.py."
        )

    dataset = TabularClassificationDataset(
        csv_path=csv_path,
        target_column=target_column,
        drop_columns=[
            "date",
            "Date",
            "id",
            "ID"
        ],
    )

    return dataset


def infer_dataset_metadata(dataset_name: str):
    dataset = build_local_dataset(dataset_name)

    x, y = dataset[0]

    if len(x.shape) == 3:
        input_shape = [1] + list(x.shape)
    else:
        input_shape = [1, int(x.numel())]

    if hasattr(dataset, "classes"):
        num_classes = len(dataset.classes)
    elif hasattr(dataset, "num_classes"):
        num_classes = dataset.num_classes
    else:
        labels = []
        for _, label in dataset:
            labels.append(int(label))
        num_classes = max(labels) + 1

    return {
        "dataset": dataset_name,
        "input_shape": input_shape,
        "num_classes": num_classes,
    }
